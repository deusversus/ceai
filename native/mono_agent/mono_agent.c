/*
 * mono_agent.dll — Mono/.NET runtime introspection agent for CE AI Suite
 *
 * Injected into Unity/Mono target processes via LoadLibrary + CreateRemoteThread.
 * Resolves Mono C API exports from mono.dll (or mono-2.0-bdwgc.dll) at runtime
 * and serves introspection commands via named pipe IPC.
 *
 * Named pipe: \\.\pipe\CEAISuite_Mono_{pid}
 * Protocol: JSON line-delimited (UTF-8). Host sends command, agent responds.
 *
 * Compile (MSVC x64):  cl.exe /LD /O2 /W4 /WX mono_agent.c /link /DLL /OUT:mono_agent.dll kernel32.lib advapi32.lib
 * Compile (TCC x64):   tcc -shared -o mono_agent.dll mono_agent.c -lkernel32 -ladvapi32
 */

#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* ── Mono C API function pointer typedefs ── */

/* Opaque Mono handles */
typedef void* MonoImage;
typedef void* MonoDomain;
typedef void* MonoAssembly;
typedef void* MonoClass;
typedef void* MonoClassField;
typedef void* MonoMethod;
typedef void* MonoObject;
typedef void* MonoType;
typedef void* MonoVTable;
typedef int   mono_bool;
typedef int   gint32;
typedef unsigned int guint32;

/* Iteration callbacks */
typedef void (*GFunc)(void* data, void* user_data);

/* Function pointer types for dynamically resolved Mono C API */
typedef MonoDomain*   (*fn_mono_get_root_domain)(void);
typedef MonoDomain*   (*fn_mono_domain_get)(void);
typedef void          (*fn_mono_domain_foreach)(GFunc func, void* user_data);
typedef const char*   (*fn_mono_domain_get_friendly_name)(MonoDomain* domain);

typedef MonoAssembly* (*fn_mono_domain_assembly_open)(MonoDomain* domain, const char* name);
typedef void          (*fn_mono_assembly_foreach)(GFunc func, void* user_data);
typedef const char*   (*fn_mono_assembly_get_name)(MonoAssembly* assembly);
typedef MonoImage*    (*fn_mono_assembly_get_image)(MonoAssembly* assembly);
typedef const char*   (*fn_mono_image_get_name)(MonoImage* image);
typedef const char*   (*fn_mono_image_get_filename)(MonoImage* image);

typedef MonoClass*    (*fn_mono_class_from_name)(MonoImage* image, const char* name_space, const char* name);
typedef const char*   (*fn_mono_class_get_name)(MonoClass* klass);
typedef const char*   (*fn_mono_class_get_namespace)(MonoClass* klass);
typedef MonoClass*    (*fn_mono_class_get_parent)(MonoClass* klass);
typedef int           (*fn_mono_class_num_fields)(MonoClass* klass);
typedef int           (*fn_mono_class_num_methods)(MonoClass* klass);

typedef MonoClassField* (*fn_mono_class_get_fields)(MonoClass* klass, void** iter);
typedef const char*   (*fn_mono_field_get_name)(MonoClassField* field);
typedef MonoType*     (*fn_mono_field_get_type)(MonoClassField* field);
typedef int           (*fn_mono_field_get_offset)(MonoClassField* field);
typedef guint32       (*fn_mono_field_get_flags)(MonoClassField* field);

typedef MonoMethod*   (*fn_mono_class_get_methods)(MonoClass* klass, void** iter);
typedef const char*   (*fn_mono_method_get_name)(MonoMethod* method);
typedef guint32       (*fn_mono_method_get_flags)(MonoMethod* method, guint32* iflags);

typedef MonoType*     (*fn_mono_method_get_return_type)(void* sig);  /* MonoMethodSignature* */
typedef void*         (*fn_mono_method_signature)(MonoMethod* method);
typedef int           (*fn_mono_signature_get_param_count)(void* sig);

typedef MonoType*     (*fn_mono_signature_get_params)(void* sig, void** iter);
typedef const char*   (*fn_mono_type_get_name)(MonoType* type);

typedef MonoVTable*   (*fn_mono_class_vtable)(MonoDomain* domain, MonoClass* klass);
typedef void*         (*fn_mono_vtable_get_static_field_data)(MonoVTable* vtable);

typedef MonoObject*   (*fn_mono_runtime_invoke)(MonoMethod* method, void* obj, void** params, MonoObject** exc);

typedef void          (*fn_mono_thread_attach)(MonoDomain* domain);

/* ── Resolved function pointers ── */
static struct {
    fn_mono_get_root_domain          get_root_domain;
    fn_mono_domain_get               domain_get;
    fn_mono_domain_foreach           domain_foreach;
    fn_mono_domain_get_friendly_name domain_get_friendly_name;
    fn_mono_assembly_foreach         assembly_foreach;
    fn_mono_assembly_get_name        assembly_get_name;
    fn_mono_assembly_get_image       assembly_get_image;
    fn_mono_image_get_name           image_get_name;
    fn_mono_image_get_filename       image_get_filename;
    fn_mono_class_from_name          class_from_name;
    fn_mono_class_get_name           class_get_name;
    fn_mono_class_get_namespace      class_get_namespace;
    fn_mono_class_get_parent         class_get_parent;
    fn_mono_class_num_fields         class_num_fields;
    fn_mono_class_num_methods        class_num_methods;
    fn_mono_class_get_fields         class_get_fields;
    fn_mono_field_get_name           field_get_name;
    fn_mono_field_get_type           field_get_type;
    fn_mono_field_get_offset         field_get_offset;
    fn_mono_field_get_flags          field_get_flags;
    fn_mono_class_get_methods        class_get_methods;
    fn_mono_method_get_name          method_get_name;
    fn_mono_method_get_flags         method_get_flags;
    fn_mono_method_signature         method_signature;
    fn_mono_method_get_return_type   method_get_return_type;  /* actually signature_get_return_type */
    fn_mono_signature_get_param_count signature_get_param_count;
    fn_mono_signature_get_params     signature_get_params;
    fn_mono_type_get_name            type_get_name;
    fn_mono_class_vtable             class_vtable;
    fn_mono_vtable_get_static_field_data vtable_get_static_field_data;
    fn_mono_runtime_invoke           runtime_invoke;
    fn_mono_thread_attach            thread_attach;
} mono = {0};

/* ── Agent state ── */

static volatile BOOL g_running = FALSE;
static HANDLE g_cmdThread = NULL;
static char g_monoVersion[64] = "unknown";

/* ── Mono API resolution ── */

static HMODULE FindMonoModule(void)
{
    /* Try common Mono DLL names */
    static const char* names[] = {
        "mono.dll",
        "mono-2.0-bdwgc.dll",
        "mono-2.0-sgen.dll",
        "MonoBleedingEdge\\mono-2.0-bdwgc.dll",
        NULL
    };
    for (int i = 0; names[i]; i++) {
        HMODULE h = GetModuleHandleA(names[i]);
        if (h) return h;
    }
    return NULL;
}

#define RESOLVE(hMono, field, name) \
    mono.field = (fn_##name)GetProcAddress(hMono, #name); \
    if (!mono.field) { /* optional — some older Mono builds may lack certain exports */ }

static BOOL ResolveMono(HMODULE hMono)
{
    /* Required exports */
    mono.get_root_domain = (fn_mono_get_root_domain)GetProcAddress(hMono, "mono_get_root_domain");
    if (!mono.get_root_domain) return FALSE;

    mono.domain_get = (fn_mono_domain_get)GetProcAddress(hMono, "mono_domain_get");
    mono.thread_attach = (fn_mono_thread_attach)GetProcAddress(hMono, "mono_thread_attach");

    /* Domain / assembly enumeration */
    RESOLVE(hMono, domain_foreach,           mono_domain_foreach);
    RESOLVE(hMono, domain_get_friendly_name, mono_domain_get_friendly_name);
    RESOLVE(hMono, assembly_foreach,         mono_assembly_foreach);
    RESOLVE(hMono, assembly_get_name,        mono_assembly_get_name);
    RESOLVE(hMono, assembly_get_image,       mono_assembly_get_image);
    RESOLVE(hMono, image_get_name,           mono_image_get_name);
    RESOLVE(hMono, image_get_filename,       mono_image_get_filename);

    /* Class introspection */
    RESOLVE(hMono, class_from_name,    mono_class_from_name);
    RESOLVE(hMono, class_get_name,     mono_class_get_name);
    RESOLVE(hMono, class_get_namespace,mono_class_get_namespace);
    RESOLVE(hMono, class_get_parent,   mono_class_get_parent);
    RESOLVE(hMono, class_num_fields,   mono_class_num_fields);
    RESOLVE(hMono, class_num_methods,  mono_class_num_methods);

    /* Field introspection */
    RESOLVE(hMono, class_get_fields,   mono_class_get_fields);
    RESOLVE(hMono, field_get_name,     mono_field_get_name);
    RESOLVE(hMono, field_get_type,     mono_field_get_type);
    RESOLVE(hMono, field_get_offset,   mono_field_get_offset);
    RESOLVE(hMono, field_get_flags,    mono_field_get_flags);

    /* Method introspection */
    RESOLVE(hMono, class_get_methods,  mono_class_get_methods);
    RESOLVE(hMono, method_get_name,    mono_method_get_name);
    RESOLVE(hMono, method_get_flags,   mono_method_get_flags);
    RESOLVE(hMono, method_signature,   mono_method_signature);
    mono.method_get_return_type = (fn_mono_method_get_return_type)GetProcAddress(hMono, "mono_signature_get_return_type");
    RESOLVE(hMono, signature_get_param_count, mono_signature_get_param_count);
    RESOLVE(hMono, signature_get_params,     mono_signature_get_params);

    /* Type names */
    RESOLVE(hMono, type_get_name,      mono_type_get_name);

    /* Static fields */
    RESOLVE(hMono, class_vtable,       mono_class_vtable);
    RESOLVE(hMono, vtable_get_static_field_data, mono_vtable_get_static_field_data);

    /* Method invocation */
    RESOLVE(hMono, runtime_invoke,     mono_runtime_invoke);

    return TRUE;
}

/* ── JSON helpers (minimal, no allocator — writes to fixed buffer) ── */

#define JSON_BUF_SIZE 65536
static char g_jsonBuf[JSON_BUF_SIZE];
static int  g_jsonLen;
static BOOL g_jsonTruncated; /* M2: detect buffer overflow */

static void json_reset(void) { g_jsonLen = 0; g_jsonBuf[0] = '\0'; g_jsonTruncated = FALSE; }
static void json_append(const char* s) {
    int len = (int)strlen(s);
    if (g_jsonLen + len < JSON_BUF_SIZE - 1) {
        memcpy(g_jsonBuf + g_jsonLen, s, len);
        g_jsonLen += len;
        g_jsonBuf[g_jsonLen] = '\0';
    } else {
        g_jsonTruncated = TRUE;
    }
}
static void json_append_str(const char* s) {
    json_append("\"");
    /* Minimal escape: replace \ with \\ and " with \" */
    for (const char* p = s; *p; p++) {
        if (*p == '\\') json_append("\\\\");
        else if (*p == '"') json_append("\\\"");
        else if (*p == '\n') json_append("\\n");
        else { char c[2] = {*p, 0}; json_append(c); }
    }
    json_append("\"");
}
static void json_append_u64(ULONG64 v) {
    char buf[32]; _snprintf(buf, sizeof(buf), "%llu", (unsigned long long)v);
    json_append(buf);
}
static void json_append_int(int v) {
    char buf[32]; _snprintf(buf, sizeof(buf), "%d", v);
    json_append(buf);
}

/* ── Enumeration callbacks (collect into temp arrays) ── */

#define MAX_ENUM_ITEMS 1024
typedef struct { void* items[MAX_ENUM_ITEMS]; int count; } EnumCollector;

static void CollectCallback(void* data, void* user_data)
{
    EnumCollector* c = (EnumCollector*)user_data;
    if (c->count < MAX_ENUM_ITEMS)
        c->items[c->count++] = data;
}

/* ── Command handlers ── */

static void HandleEnumDomains(void)
{
    json_reset();
    json_append("{\"ok\":true,\"domains\":[");

    EnumCollector coll = {0};
    if (mono.domain_foreach) {
        mono.domain_foreach(CollectCallback, &coll);
    } else {
        /* Fallback: just the root domain */
        MonoDomain* root = mono.get_root_domain();
        if (root) { coll.items[0] = root; coll.count = 1; }
    }

    for (int i = 0; i < coll.count; i++) {
        MonoDomain* dom = (MonoDomain*)coll.items[i];
        const char* name = mono.domain_get_friendly_name ? mono.domain_get_friendly_name(dom) : "root";
        if (i > 0) json_append(",");
        json_append("{\"handle\":");
        json_append_u64((ULONG64)(uintptr_t)dom);
        json_append(",\"name\":");
        json_append_str(name ? name : "");
        json_append(",\"assembly_count\":0}");  /* count filled lazily */
    }

    json_append("]}\n");
}

static void HandleEnumAssemblies(ULONG64 domainHandle)
{
    MonoDomain* domain = (MonoDomain*)(uintptr_t)domainHandle;
    /* L4: Mono's assembly_foreach enumerates all assemblies globally regardless of domain.
     * The Mono C API does not offer a domain-scoped assembly enumeration function.
     * This matches CE's behavior — mono_enumAssemblies returns all loaded assemblies. */
    (void)domain;

    json_reset();
    json_append("{\"ok\":true,\"assemblies\":[");

    EnumCollector coll = {0};
    if (mono.assembly_foreach)
        mono.assembly_foreach(CollectCallback, &coll);

    for (int i = 0; i < coll.count; i++) {
        MonoAssembly* asm_ = (MonoAssembly*)coll.items[i];
        MonoImage* img = mono.assembly_get_image ? mono.assembly_get_image(asm_) : NULL;
        const char* name = mono.assembly_get_name ? mono.assembly_get_name(asm_) : "";
        const char* imgName = (img && mono.image_get_name) ? mono.image_get_name(img) : "";

        if (i > 0) json_append(",");
        json_append("{\"handle\":");
        json_append_u64((ULONG64)(uintptr_t)asm_);
        json_append(",\"image_handle\":");
        json_append_u64((ULONG64)(uintptr_t)img);
        json_append(",\"name\":");
        json_append_str(name ? name : "");
        json_append(",\"full_name\":");
        json_append_str(imgName ? imgName : "");
        json_append("}");
    }

    json_append("]}\n");
}

static void HandleFindClass(ULONG64 imageHandle, const char* ns, const char* name)
{
    json_reset();
    if (!mono.class_from_name) {
        json_append("{\"ok\":false,\"error\":\"mono_class_from_name not resolved\"}\n");
        return;
    }

    MonoImage* image = (MonoImage*)(uintptr_t)imageHandle;
    MonoClass* klass = mono.class_from_name(image, ns, name);
    if (!klass) {
        json_append("{\"ok\":true,\"class\":null}\n");
        return;
    }

    const char* cn = mono.class_get_name ? mono.class_get_name(klass) : name;
    const char* cns = mono.class_get_namespace ? mono.class_get_namespace(klass) : ns;
    MonoClass* parent = mono.class_get_parent ? mono.class_get_parent(klass) : NULL;
    int nf = mono.class_num_fields ? mono.class_num_fields(klass) : 0;
    int nm = mono.class_num_methods ? mono.class_num_methods(klass) : 0;

    json_append("{\"ok\":true,\"class\":{\"handle\":");
    json_append_u64((ULONG64)(uintptr_t)klass);
    json_append(",\"namespace\":");
    json_append_str(cns ? cns : "");
    json_append(",\"name\":");
    json_append_str(cn ? cn : "");
    json_append(",\"parent_handle\":");
    json_append_u64((ULONG64)(uintptr_t)parent);
    json_append(",\"field_count\":");
    json_append_int(nf);
    json_append(",\"method_count\":");
    json_append_int(nm);
    json_append("}}\n");
}

static void HandleEnumFields(ULONG64 classHandle)
{
    MonoClass* klass = (MonoClass*)(uintptr_t)classHandle;
    json_reset();
    json_append("{\"ok\":true,\"fields\":[");

    if (mono.class_get_fields && mono.field_get_name) {
        void* iter = NULL;
        MonoClassField* field;
        int first = 1;
        while ((field = mono.class_get_fields(klass, &iter)) != NULL) {
            const char* fname = mono.field_get_name(field);
            const char* tname = "";
            if (mono.field_get_type && mono.type_get_name) {
                MonoType* ft = mono.field_get_type(field);
                if (ft) tname = mono.type_get_name(ft);
            }
            int offset = mono.field_get_offset ? mono.field_get_offset(field) : -1;
            guint32 flags = mono.field_get_flags ? mono.field_get_flags(field) : 0;
            int isStatic = (flags & 0x10) ? 1 : 0; /* FIELD_ATTRIBUTE_STATIC = 0x10 */

            if (!first) json_append(",");
            first = 0;
            json_append("{\"handle\":");
            json_append_u64((ULONG64)(uintptr_t)field);
            json_append(",\"name\":");
            json_append_str(fname ? fname : "");
            json_append(",\"type_name\":");
            json_append_str(tname ? tname : "");
            json_append(",\"offset\":");
            json_append_int(offset);
            json_append(",\"is_static\":");
            json_append(isStatic ? "true" : "false");
            json_append("}");
        }
    }

    json_append("]}\n");
}

static void HandleEnumMethods(ULONG64 classHandle)
{
    MonoClass* klass = (MonoClass*)(uintptr_t)classHandle;
    json_reset();
    json_append("{\"ok\":true,\"methods\":[");

    if (mono.class_get_methods && mono.method_get_name) {
        void* iter = NULL;
        MonoMethod* method;
        int first = 1;
        while ((method = mono.class_get_methods(klass, &iter)) != NULL) {
            const char* mname = mono.method_get_name(method);
            const char* retType = "void";
            int paramCount = 0;
            guint32 flags = 0, iflags = 0;

            if (mono.method_signature) {
                void* sig = mono.method_signature(method);
                if (sig) {
                    if (mono.method_get_return_type && mono.type_get_name) {
                        MonoType* rt = mono.method_get_return_type(sig);
                        if (rt) retType = mono.type_get_name(rt);
                    }
                    if (mono.signature_get_param_count)
                        paramCount = mono.signature_get_param_count(sig);
                }
            }
            if (mono.method_get_flags)
                flags = mono.method_get_flags(method, &iflags);
            int isStatic = (flags & 0x10) ? 1 : 0; /* METHOD_ATTRIBUTE_STATIC = 0x10 */

            if (!first) json_append(",");
            first = 0;
            json_append("{\"handle\":");
            json_append_u64((ULONG64)(uintptr_t)method);
            json_append(",\"name\":");
            json_append_str(mname ? mname : "");
            json_append(",\"return_type\":");
            json_append_str(retType ? retType : "void");
            json_append(",\"is_static\":");
            json_append(isStatic ? "true" : "false");
            json_append(",\"parameter_types\":[");
            /* L5: enumerate parameter types via mono_signature_get_params */
            if (mono.method_signature && mono.signature_get_params && mono.type_get_name) {
                void* sig2 = mono.method_signature(method);
                if (sig2) {
                    void* piter = NULL;
                    MonoType* ptype;
                    int pfirst = 1;
                    while ((ptype = mono.signature_get_params(sig2, &piter)) != NULL) {
                        const char* ptname = mono.type_get_name(ptype);
                        if (!pfirst) json_append(",");
                        pfirst = 0;
                        json_append_str(ptname ? ptname : "?");
                    }
                }
            }
            json_append("]");
            json_append("}");
        }
    }

    json_append("]}\n");
}

static void HandleGetStaticField(ULONG64 classHandle, ULONG64 fieldHandle, int size)
{
    json_reset();
    MonoClass* klass = (MonoClass*)(uintptr_t)classHandle;
    MonoClassField* field = (MonoClassField*)(uintptr_t)fieldHandle;

    if (!mono.class_vtable || !mono.vtable_get_static_field_data || !mono.domain_get) {
        json_append("{\"ok\":false,\"error\":\"static field access not available\"}\n");
        return;
    }

    MonoDomain* domain = mono.domain_get();
    MonoVTable* vtable = mono.class_vtable(domain, klass);
    if (!vtable) {
        json_append("{\"ok\":false,\"error\":\"vtable not found\"}\n");
        return;
    }

    void* staticData = mono.vtable_get_static_field_data(vtable);
    if (!staticData) {
        json_append("{\"ok\":false,\"error\":\"no static data\"}\n");
        return;
    }

    int offset = mono.field_get_offset ? mono.field_get_offset(field) : 0;
    /* M3: bounds check — cap size and reject obviously invalid offsets */
    if (size < 1) size = 1;
    if (size > 64) size = 64;
    if (offset < 0 || offset > 0x7FFFFFFF) {
        json_append("{\"ok\":false,\"error\":\"invalid field offset\"}\n");
        return;
    }
    unsigned char* ptr = (unsigned char*)staticData + offset;

    /* Base64-encode the raw bytes */
    static const char b64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    char encoded[256];
    int ei = 0;
    if (size > 64) size = 64; /* cap */
    for (int i = 0; i < size; i += 3) {
        unsigned int n = (unsigned int)ptr[i] << 16;
        if (i+1 < size) n |= (unsigned int)ptr[i+1] << 8;
        if (i+2 < size) n |= (unsigned int)ptr[i+2];
        encoded[ei++] = b64[(n >> 18) & 0x3F];
        encoded[ei++] = b64[(n >> 12) & 0x3F];
        encoded[ei++] = (i+1 < size) ? b64[(n >> 6) & 0x3F] : '=';
        encoded[ei++] = (i+2 < size) ? b64[n & 0x3F] : '=';
    }
    encoded[ei] = '\0';

    json_append("{\"ok\":true,\"data\":\"");
    json_append(encoded);
    json_append("\"}\n");
}

/* C2 audit fix: set_static_field handler */
static void HandleSetStaticField(ULONG64 classHandle, ULONG64 fieldHandle, const char* b64Data)
{
    json_reset();
    MonoClass* klass = (MonoClass*)(uintptr_t)classHandle;
    MonoClassField* field = (MonoClassField*)(uintptr_t)fieldHandle;

    if (!mono.class_vtable || !mono.vtable_get_static_field_data || !mono.domain_get) {
        json_append("{\"ok\":false,\"error\":\"static field write not available\"}\n");
        return;
    }

    MonoDomain* domain = mono.domain_get();
    MonoVTable* vtable = mono.class_vtable(domain, klass);
    if (!vtable) { json_append("{\"ok\":false,\"error\":\"vtable not found\"}\n"); return; }

    void* staticData = mono.vtable_get_static_field_data(vtable);
    if (!staticData) { json_append("{\"ok\":false,\"error\":\"no static data\"}\n"); return; }

    int offset = mono.field_get_offset ? mono.field_get_offset(field) : 0;
    unsigned char* ptr = (unsigned char*)staticData + offset;

    /* Base64-decode the data */
    static const unsigned char b64d[256] = {
        ['A']=0,['B']=1,['C']=2,['D']=3,['E']=4,['F']=5,['G']=6,['H']=7,
        ['I']=8,['J']=9,['K']=10,['L']=11,['M']=12,['N']=13,['O']=14,['P']=15,
        ['Q']=16,['R']=17,['S']=18,['T']=19,['U']=20,['V']=21,['W']=22,['X']=23,
        ['Y']=24,['Z']=25,['a']=26,['b']=27,['c']=28,['d']=29,['e']=30,['f']=31,
        ['g']=32,['h']=33,['i']=34,['j']=35,['k']=36,['l']=37,['m']=38,['n']=39,
        ['o']=40,['p']=41,['q']=42,['r']=43,['s']=44,['t']=45,['u']=46,['v']=47,
        ['w']=48,['x']=49,['y']=50,['z']=51,['0']=52,['1']=53,['2']=54,['3']=55,
        ['4']=56,['5']=57,['6']=58,['7']=59,['8']=60,['9']=61,['+']=62,['/']=63
    };
    int len = (int)strlen(b64Data);
    int outLen = 0;
    for (int i = 0; i < len && outLen < 64; i += 4) {
        unsigned int n = (b64d[(unsigned char)b64Data[i]] << 18) |
                         (b64d[(unsigned char)b64Data[i+1]] << 12) |
                         (b64d[(unsigned char)b64Data[i+2]] << 6) |
                          b64d[(unsigned char)b64Data[i+3]];
        ptr[outLen++] = (unsigned char)(n >> 16);
        if (b64Data[i+2] != '=' && outLen < 64) ptr[outLen++] = (unsigned char)(n >> 8);
        if (b64Data[i+3] != '=' && outLen < 64) ptr[outLen++] = (unsigned char)n;
    }

    json_append("{\"ok\":true}\n");
}

static void HandleInvokeMethod(ULONG64 methodHandle, ULONG64 instanceHandle,
                               const char* cmdLine)
{
    json_reset();
    if (!mono.runtime_invoke) {
        json_append("{\"ok\":false,\"error\":\"mono_runtime_invoke not resolved\"}\n");
        return;
    }

    MonoMethod* method = (MonoMethod*)(uintptr_t)methodHandle;
    void* instance = (void*)(uintptr_t)instanceHandle;
    MonoObject* exc = NULL;

    /* C3 audit fix: Parse args array from command JSON.
     * Format: "args":[123,456,...] — each element is a pointer-sized value.
     * We pass them as void** to mono_runtime_invoke. */
    void* argPtrs[16] = {0};
    void** argsParam = NULL;
    {
        const char* argsStart = strstr(cmdLine, "\"args\":[");
        if (argsStart) {
            argsStart += 8; /* skip "args":[ */
            int argCount = 0;
            while (*argsStart && *argsStart != ']' && argCount < 16) {
                while (*argsStart == ' ' || *argsStart == ',') argsStart++;
                if (*argsStart == ']') break;
                ULONG64 v = 0;
                while (*argsStart >= '0' && *argsStart <= '9') {
                    v = v * 10 + (*argsStart - '0');
                    argsStart++;
                }
                argPtrs[argCount++] = (void*)(uintptr_t)v;
            }
            if (argCount > 0) argsParam = argPtrs;
        }
    }

    MonoObject* result = mono.runtime_invoke(method, instance, argsParam, &exc);
    if (exc) {
        json_append("{\"ok\":false,\"error\":\"managed exception thrown\"}\n");
        return;
    }

    json_append("{\"ok\":true,\"return_value\":");
    json_append_u64((ULONG64)(uintptr_t)result);
    json_append("}\n");
}

/* ── Named pipe command loop ── */

static BOOL ParseU64(const char* json, const char* key, ULONG64* out)
{
    char search[128];
    _snprintf(search, sizeof(search), "\"%s\":", key);
    const char* p = strstr(json, search);
    if (!p) return FALSE;
    p += strlen(search);
    while (*p == ' ') p++;
    *out = 0;
    while (*p >= '0' && *p <= '9') {
        *out = *out * 10 + (*p - '0');
        p++;
    }
    return TRUE;
}

static BOOL ParseString(const char* json, const char* key, char* out, int maxLen)
{
    char search[128];
    _snprintf(search, sizeof(search), "\"%s\":\"", key);
    const char* p = strstr(json, search);
    if (!p) return FALSE;
    p += strlen(search);
    int i = 0;
    while (*p && *p != '"' && i < maxLen - 1) {
        if (*p == '\\' && *(p+1)) { p++; } /* skip escape */
        out[i++] = *p++;
    }
    out[i] = '\0';
    return TRUE;
}

static BOOL ParseInt(const char* json, const char* key, int* out)
{
    ULONG64 v;
    if (!ParseU64(json, key, &v)) return FALSE;
    *out = (int)v;
    return TRUE;
}

static void DispatchCommand(const char* cmdLine, HANDLE hPipe)
{
    DWORD written;
    char cmdName[64] = "";
    ParseString(cmdLine, "cmd", cmdName, sizeof(cmdName));

    if (strcmp(cmdName, "enum_domains") == 0) {
        HandleEnumDomains();
    }
    else if (strcmp(cmdName, "enum_assemblies") == 0) {
        ULONG64 domain = 0;
        ParseU64(cmdLine, "domain", &domain);
        HandleEnumAssemblies(domain);
    }
    else if (strcmp(cmdName, "find_class") == 0) {
        ULONG64 image = 0;
        char ns[256] = "", name[256] = "";
        ParseU64(cmdLine, "image", &image);
        ParseString(cmdLine, "ns", ns, sizeof(ns));
        ParseString(cmdLine, "name", name, sizeof(name));
        HandleFindClass(image, ns, name);
    }
    else if (strcmp(cmdName, "enum_fields") == 0) {
        ULONG64 klass = 0;
        ParseU64(cmdLine, "class", &klass);
        HandleEnumFields(klass);
    }
    else if (strcmp(cmdName, "enum_methods") == 0) {
        ULONG64 klass = 0;
        ParseU64(cmdLine, "class", &klass);
        HandleEnumMethods(klass);
    }
    else if (strcmp(cmdName, "get_static_field") == 0) {
        ULONG64 klass = 0, field = 0;
        int size = 8;
        ParseU64(cmdLine, "class", &klass);
        ParseU64(cmdLine, "field", &field);
        ParseInt(cmdLine, "size", &size);
        HandleGetStaticField(klass, field, size);
    }
    else if (strcmp(cmdName, "set_static_field") == 0) {
        ULONG64 klass = 0, field = 0;
        char b64Data[512] = "";
        ParseU64(cmdLine, "class", &klass);
        ParseU64(cmdLine, "field", &field);
        ParseString(cmdLine, "data", b64Data, sizeof(b64Data));
        HandleSetStaticField(klass, field, b64Data);
    }
    else if (strcmp(cmdName, "invoke_method") == 0) {
        ULONG64 method = 0, instance = 0;
        ParseU64(cmdLine, "method", &method);
        ParseU64(cmdLine, "instance", &instance);
        HandleInvokeMethod(method, instance, cmdLine);
    }
    else if (strcmp(cmdName, "shutdown") == 0) {
        json_reset();
        json_append("{\"ok\":true}\n");
        WriteFile(hPipe, g_jsonBuf, (DWORD)g_jsonLen, &written, NULL);
        FlushFileBuffers(hPipe);
        g_running = FALSE;
        return;
    }
    else {
        json_reset();
        json_append("{\"ok\":false,\"error\":\"unknown command: ");
        json_append(cmdName);
        json_append("\"}\n");
    }

    /* M2: If response was truncated, replace with an error to avoid malformed JSON */
    if (g_jsonTruncated) {
        json_reset();
        json_append("{\"ok\":false,\"error\":\"response truncated (exceeded 64KB buffer)\"}\n");
    }

    WriteFile(hPipe, g_jsonBuf, (DWORD)g_jsonLen, &written, NULL);
    FlushFileBuffers(hPipe);
}

/* ── Agent init thread (runs outside loader lock) ── */

static DWORD WINAPI AgentInitThread(LPVOID param)
{
    (void)param;

    /* 1. Find and resolve Mono API */
    HMODULE hMono = FindMonoModule();
    if (!hMono) {
        /* mono.dll not loaded — this process may not be a Mono game. Exit gracefully. */
        g_running = FALSE;
        return 1;
    }

    if (!ResolveMono(hMono)) {
        g_running = FALSE;
        return 2;
    }

    /* 2. Attach this thread to the Mono runtime so we can call Mono API safely */
    if (mono.thread_attach && mono.get_root_domain) {
        MonoDomain* root = mono.get_root_domain();
        if (root) mono.thread_attach(root);
    }

    /* 3. Detect Mono version (best-effort) */
    {
        typedef const char* (*fn_mono_get_version)(void);
        fn_mono_get_version getVer = (fn_mono_get_version)GetProcAddress(hMono, "mono_get_version");
        if (getVer) {
            const char* ver = getVer();
            if (ver) _snprintf(g_monoVersion, sizeof(g_monoVersion), "%s", ver);
        }
    }

    /* 4. Create named pipe server with security DACL (M1: restrict to current user) */
    char pipeName[128];
    _snprintf(pipeName, sizeof(pipeName), "\\\\.\\pipe\\CEAISuite_Mono_%u", GetCurrentProcessId());

    /* Build a SECURITY_ATTRIBUTES that restricts pipe access to the current process owner.
     * The SDDL string "D:(A;;GA;;;OW)" grants Generic All to the Owner only. */
    SECURITY_ATTRIBUTES sa = {0};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = FALSE;
    BOOL saOk = ConvertStringSecurityDescriptorToSecurityDescriptorA(
        "D:(A;;GA;;;OW)", 1 /* SDDL_REVISION_1 */, &sa.lpSecurityDescriptor, NULL);

    HANDLE hPipe = CreateNamedPipeA(
        pipeName,
        PIPE_ACCESS_DUPLEX | 0x00080000 /* FILE_FLAG_FIRST_PIPE_INSTANCE — prevent squatting */,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1,       /* max instances */
        JSON_BUF_SIZE, JSON_BUF_SIZE,
        0, saOk ? &sa : NULL);

    if (saOk && sa.lpSecurityDescriptor)
        LocalFree(sa.lpSecurityDescriptor);

    if (hPipe == INVALID_HANDLE_VALUE) {
        g_running = FALSE;
        return 3;
    }

    /* 5. Wait for host to connect */
    if (!ConnectNamedPipe(hPipe, NULL)) {
        if (GetLastError() != ERROR_PIPE_CONNECTED) {
            CloseHandle(hPipe);
            g_running = FALSE;
            return 4;
        }
    }

    /* 6. Send hello line with Mono version */
    {
        json_reset();
        json_append("{\"ok\":true,\"mono_version\":\"");
        json_append(g_monoVersion);
        json_append("\"}\n");
        DWORD written;
        WriteFile(hPipe, g_jsonBuf, (DWORD)g_jsonLen, &written, NULL);
        FlushFileBuffers(hPipe);
    }

    /* 7. Command loop — read lines, dispatch */
    char lineBuf[JSON_BUF_SIZE];
    int linePos = 0;

    while (g_running) {
        DWORD bytesRead;
        char readBuf[4096];
        if (!ReadFile(hPipe, readBuf, sizeof(readBuf), &bytesRead, NULL) || bytesRead == 0)
            break;

        for (DWORD i = 0; i < bytesRead; i++) {
            if (readBuf[i] == '\n') {
                lineBuf[linePos] = '\0';
                if (linePos > 0)
                    DispatchCommand(lineBuf, hPipe);
                linePos = 0;
            } else if (linePos < JSON_BUF_SIZE - 2) {
                lineBuf[linePos++] = readBuf[i];
            }
        }
    }

    /* Cleanup */
    DisconnectNamedPipe(hPipe);
    CloseHandle(hPipe);
    return 0;
}

/* ── DLL entry point ── */

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved)
{
    (void)hModule;
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        g_running = TRUE;
        g_cmdThread = CreateThread(NULL, 0, AgentInitThread, NULL, 0, NULL);
        if (!g_cmdThread) return FALSE;
    }
    else if (reason == DLL_PROCESS_DETACH) {
        g_running = FALSE;
        if (g_cmdThread) {
            WaitForSingleObject(g_cmdThread, 3000);
            CloseHandle(g_cmdThread);
            g_cmdThread = NULL;
        }
    }
    return TRUE;
}
