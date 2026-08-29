using IndustrialDAQ.Infrastructure;

namespace IndustrialDAQ.Tests;

public sealed class HistoricalValueCodecTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(42)]
    [InlineData(12.5d)]
    [InlineData("运行")]
    public void Codec_RestoresOriginalType(object input)
    {
        var encoded = HistoricalValueCodec.Serialize(input);
        var restored = HistoricalValueCodec.Deserialize(encoded.Value, encoded.ValueType);
        Assert.Equal(input, restored);
        Assert.Equal(input.GetType(), restored?.GetType());
    }
}
