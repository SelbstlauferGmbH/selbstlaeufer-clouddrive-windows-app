using CloudDrive.Core.SyncEngine;
using Shouldly;

namespace CloudDrive.Core.Tests.SyncEngine;

public sealed class TransientFilePolicyTests
{
    [Theory]
    [InlineData("~$Report.docx")]
    [InlineData("~WRL0001.tmp")]
    [InlineData("~WRD0000.tmp")]
    [InlineData("~DF1234.tmp")]
    [InlineData(".clouddrive-download-123.tmp")]
    public void ShouldIgnoreName_RecognizesOfficeTransientFiles(string name)
    {
        TransientFilePolicy.ShouldIgnoreName(name).ShouldBeTrue();
    }

    [Fact]
    public void IsProviderInternalName_RecognizesProviderTemporaryFiles()
    {
        TransientFilePolicy.IsProviderInternalName(".clouddrive-download-123.tmp").ShouldBeTrue();
        TransientFilePolicy.IsProviderInternalName("~WRL0001.tmp").ShouldBeFalse();
    }

    [Theory]
    [InlineData("Report.docx")]
    [InlineData("archive.tmp")]
    [InlineData("~notes.txt")]
    public void ShouldIgnoreName_KeepsDurableUserFiles(string name)
    {
        TransientFilePolicy.ShouldIgnoreName(name).ShouldBeFalse();
    }
}
