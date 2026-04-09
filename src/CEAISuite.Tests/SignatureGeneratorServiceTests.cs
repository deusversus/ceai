using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

public class SignatureGeneratorServiceTests
{
    private static StubEngineFacade CreateFacadeWithBytes(nuint address, byte[] data)
    {
        var facade = new StubEngineFacade();
        facade.WriteMemoryDirect(address, data);
        return facade;
    }

    [Fact]
    public async Task GenerateAsync_PlainBytes_NoWildcards()
    {
        // Bytes that don't match any relocation pattern
        var bytes = new byte[] { 0x55, 0x48, 0x89, 0xE5, 0x90, 0x90, 0x90, 0x90 };
        var facade = CreateFacadeWithBytes((nuint)0x1000, bytes);
        var svc = new SignatureGeneratorService(facade);

        var result = await svc.GenerateAsync(1, (nuint)0x1000, 8);

        Assert.Equal((nuint)0x1000, result.Address);
        Assert.Equal(8, result.Length);
        Assert.DoesNotContain("??", result.Pattern);
        Assert.Contains("55", result.Pattern);
    }

    [Fact]
    public async Task GenerateAsync_CallRel32_WildcardsOffsetBytes()
    {
        // E8 xx xx xx xx (CALL rel32) followed by NOP
        var bytes = new byte[] { 0xE8, 0x12, 0x34, 0x56, 0x78, 0x90, 0x90, 0x90 };
        var facade = CreateFacadeWithBytes((nuint)0x1000, bytes);
        var svc = new SignatureGeneratorService(facade);

        var result = await svc.GenerateAsync(1, (nuint)0x1000, 8);

        // E8 should stay, the 4 bytes after it should be wildcards
        Assert.StartsWith("E8 ?? ?? ?? ??", result.Pattern);
        Assert.Contains("90", result.Pattern);
    }

    [Fact]
    public async Task GenerateAsync_JmpRel32_WildcardsOffsetBytes()
    {
        // E9 xx xx xx xx (JMP rel32) followed by data
        var bytes = new byte[] { 0xE9, 0xAA, 0xBB, 0xCC, 0xDD, 0x41, 0x42, 0x43 };
        var facade = CreateFacadeWithBytes((nuint)0x1000, bytes);
        var svc = new SignatureGeneratorService(facade);

        var result = await svc.GenerateAsync(1, (nuint)0x1000, 8);

        Assert.StartsWith("E9 ?? ?? ?? ??", result.Pattern);
    }

    [Fact]
    public async Task GenerateAsync_DescriptionContainsAddress()
    {
        var bytes = new byte[] { 0x90, 0x90, 0x90, 0x90 };
        var facade = CreateFacadeWithBytes((nuint)0x401000, bytes);
        var svc = new SignatureGeneratorService(facade);

        var result = await svc.GenerateAsync(1, (nuint)0x401000, 4);

        Assert.Contains("401000", result.Description);
    }

    [Fact]
    public async Task TestUniquenessAsync_SingleMatch_ReturnsOne()
    {
        var facade = new StubEngineFacade();
        // Set up module at 0x400000, size 16
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 16) };
        // Write bytes at the module base
        facade.WriteMemoryDirect((nuint)0x400000, new byte[] { 0x55, 0x48, 0x89, 0xE5, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        var svc = new SignatureGeneratorService(facade);
        var count = await svc.TestUniquenessAsync(1, "game.exe", "55 48 89 E5");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task TestUniquenessAsync_NoMatch_ReturnsZero()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 16) };
        facade.WriteMemoryDirect((nuint)0x400000, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        var svc = new SignatureGeneratorService(facade);
        var count = await svc.TestUniquenessAsync(1, "game.exe", "55 48 89 E5");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task TestUniquenessAsync_ModuleNotFound_ReturnsNegativeOne()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 4096) };

        var svc = new SignatureGeneratorService(facade);
        var count = await svc.TestUniquenessAsync(1, "nonexistent.dll", "55 48");

        Assert.Equal(-1, count);
    }

    [Fact]
    public async Task TestUniquenessAsync_WildcardPattern_MatchesAnyByte()
    {
        var facade = new StubEngineFacade();
        facade.AttachModules = new[] { new ModuleDescriptor("game.exe", (nuint)0x400000, 8) };
        facade.WriteMemoryDirect((nuint)0x400000, new byte[] { 0x55, 0xFF, 0x89, 0xE5, 0x00, 0x00, 0x00, 0x00 });

        var svc = new SignatureGeneratorService(facade);
        // Use ?? wildcard for the second byte
        var count = await svc.TestUniquenessAsync(1, "game.exe", "55 ?? 89 E5");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GenerateAsync_ConditionalJmp_WildcardsOffsets()
    {
        // 0F 85 xx xx xx xx (JNE rel32) followed by NOPs
        var bytes = new byte[] { 0x0F, 0x85, 0x11, 0x22, 0x33, 0x44, 0x90, 0x90 };
        var facade = CreateFacadeWithBytes((nuint)0x1000, bytes);
        var svc = new SignatureGeneratorService(facade);

        var result = await svc.GenerateAsync(1, (nuint)0x1000, 8);

        // 0F 85 should stay, the next 4 bytes should be wildcarded
        Assert.Contains("0F 85 ?? ?? ?? ??", result.Pattern);
    }
}
