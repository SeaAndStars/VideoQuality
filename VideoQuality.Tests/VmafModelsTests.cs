using VideoQuality.Core.Scoring;

namespace VideoQuality.Tests;

public class VmafModelsTests
{
    [Fact]
    public void Default_is_neg()
    {
        Assert.Equal(VmafModels.Neg, VmafModels.Default);
        Assert.Equal(VmafModels.Neg, new VmafOptions().Model);
    }

    [Fact]
    public void Display_names_distinguish_neg()
    {
        Assert.Equal("VMAF NEG", VmafModels.DisplayName(VmafModels.Neg));
        Assert.Equal("VMAF", VmafModels.DisplayName(VmafModels.Standard));
        Assert.Equal("VMAF 4K", VmafModels.DisplayName(VmafModels.Uhd));
        Assert.Equal("NEG", VmafModels.ShortName(VmafModels.Neg));
    }

    [Fact]
    public void Unknown_models_pass_through()
    {
        Assert.Equal("custom.json", VmafModels.DisplayName("custom.json"));
        Assert.Equal("custom.json", VmafModels.ShortName("custom.json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_models_read_as_plain_vmaf(string? model)
    {
        Assert.Equal("VMAF", VmafModels.DisplayName(model));
        Assert.Equal("VMAF", VmafModels.ShortName(model));
        Assert.Equal("STD", VmafModels.ShortName(VmafModels.Standard));
        Assert.Equal("4K", VmafModels.ShortName(VmafModels.Uhd));
    }
}
