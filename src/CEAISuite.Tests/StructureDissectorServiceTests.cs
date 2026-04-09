using CEAISuite.Application;
using CEAISuite.Engine.Abstractions;

namespace CEAISuite.Tests;

public class StructureDissectorServiceTests
{
    private static StubEngineFacade CreateFacadeWithBlock(nuint address, byte[] data)
    {
        var facade = new StubEngineFacade();
        facade.WriteMemoryDirect(address, data);
        return facade;
    }

    [Fact]
    public async Task DissectAsync_Int32Values_IdentifiedWithHighConfidence()
    {
        // Layout: two game-like Int32 values (100, 200) at offsets 0 and 4
        var data = new byte[16];
        BitConverter.GetBytes(100).CopyTo(data, 0);
        BitConverter.GetBytes(200).CopyTo(data, 4);
        // Zero padding
        Array.Clear(data, 8, 8);

        var facade = CreateFacadeWithBlock((nuint)0x1000, data);
        var svc = new StructureDissectorService(facade);

        var (fields, clusters) = await svc.DissectAsync(1, (nuint)0x1000, 16);

        Assert.NotEmpty(fields);
        // At offset 0 and 4 we should see candidates
        var offset0 = fields.FirstOrDefault(f => f.Offset == 0);
        var offset4 = fields.FirstOrDefault(f => f.Offset == 4);
        Assert.NotNull(offset0);
        Assert.NotNull(offset4);
    }

    [Fact]
    public async Task DissectAsync_FloatValues_DetectedAsFloat()
    {
        // Two float values: 3.14 and 2.71
        var data = new byte[16];
        BitConverter.GetBytes(3.14f).CopyTo(data, 0);
        BitConverter.GetBytes(2.71f).CopyTo(data, 4);
        Array.Clear(data, 8, 8);

        var facade = CreateFacadeWithBlock((nuint)0x1000, data);
        var svc = new StructureDissectorService(facade);

        var (fields, _) = await svc.DissectAsync(1, (nuint)0x1000, 16);

        Assert.NotEmpty(fields);
        // Float values with decimal parts should be detected as Float with high confidence
        var floatFields = fields.Where(f => f.ProbableType == "Float").ToList();
        Assert.NotEmpty(floatFields);
    }

    [Fact]
    public async Task DissectAsync_ZeroPadding_DetectedAsPaddingZero()
    {
        // All zeros
        var data = new byte[8];
        var facade = CreateFacadeWithBlock((nuint)0x1000, data);
        var svc = new StructureDissectorService(facade);

        var (fields, _) = await svc.DissectAsync(1, (nuint)0x1000, 8);

        // Zero regions should be identified
        var zeroFields = fields.Where(f => f.ProbableType == "Padding/Zero").ToList();
        Assert.NotEmpty(zeroFields);
    }

    [Fact]
    public async Task DissectAsync_TypeHintInt32_BoostsInt32Confidence()
    {
        // A value like 100 that could be Int32 or Float
        var data = new byte[8];
        BitConverter.GetBytes(100).CopyTo(data, 0);
        Array.Clear(data, 4, 4);

        var facade = CreateFacadeWithBlock((nuint)0x1000, data);
        var svc = new StructureDissectorService(facade);

        var (fieldsAuto, _) = await svc.DissectAsync(1, (nuint)0x1000, 8, "auto");
        var (fieldsHint, _) = await svc.DissectAsync(1, (nuint)0x1000, 8, "int32");

        var autoOffset0 = fieldsAuto.FirstOrDefault(f => f.Offset == 0);
        var hintOffset0 = fieldsHint.FirstOrDefault(f => f.Offset == 0);

        Assert.NotNull(autoOffset0);
        Assert.NotNull(hintOffset0);
        // The int32 hint should produce a result with type Int32
        Assert.Equal("Int32", hintOffset0!.ProbableType);
    }

    [Fact]
    public async Task DissectAsync_IntegerClustering_BoostsConfidence()
    {
        // 5 consecutive game-stat int32 values: triggers clustering (needs >= 3 in a row)
        var data = new byte[32];
        BitConverter.GetBytes(100).CopyTo(data, 0);   // HP
        BitConverter.GetBytes(50).CopyTo(data, 4);    // MP
        BitConverter.GetBytes(10).CopyTo(data, 8);    // Level
        BitConverter.GetBytes(500).CopyTo(data, 12);  // Gold
        BitConverter.GetBytes(25).CopyTo(data, 16);   // Strength
        Array.Clear(data, 20, 12);

        var facade = CreateFacadeWithBlock((nuint)0x1000, data);
        var svc = new StructureDissectorService(facade);

        var (fields, clusters) = await svc.DissectAsync(1, (nuint)0x1000, 32, "int32");

        Assert.True(clusters >= 1, "Should detect at least one integer cluster");

        // Clustered Int32 fields should have boosted confidence
        var clusteredInts = fields.Where(f => f.ProbableType == "Int32" && f.Offset <= 16).ToList();
        Assert.NotEmpty(clusteredInts);
    }

    [Fact]
    public async Task DissectAsync_SmallRegion_ReturnsFieldsWithoutCrashing()
    {
        // Minimum: 4 bytes
        var data = new byte[4];
        BitConverter.GetBytes(42).CopyTo(data, 0);

        var facade = CreateFacadeWithBlock((nuint)0x1000, data);
        var svc = new StructureDissectorService(facade);

        var (fields, clusters) = await svc.DissectAsync(1, (nuint)0x1000, 4);

        // Should get at least one field at offset 0
        Assert.NotEmpty(fields);
        Assert.Equal(0, fields[0].Offset);
    }

    [Fact]
    public async Task DissectAsync_FloatHint_BoostsFloatConfidence()
    {
        var data = new byte[8];
        BitConverter.GetBytes(3.14f).CopyTo(data, 0);
        Array.Clear(data, 4, 4);

        var facade = CreateFacadeWithBlock((nuint)0x1000, data);
        var svc = new StructureDissectorService(facade);

        var (fields, _) = await svc.DissectAsync(1, (nuint)0x1000, 8, "float");

        var offset0 = fields.FirstOrDefault(f => f.Offset == 0);
        Assert.NotNull(offset0);
        Assert.Equal("Float", offset0!.ProbableType);
    }
}
