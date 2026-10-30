namespace ServiceLib.Tests.Manager;

public class CoreAdminManagerTests
{
    [Test]
    [Arguments("/Applications/v2rayN.app/Contents/MacOS/sing-box", "'/Applications/v2rayN.app/Contents/MacOS/sing-box'")]
    [Arguments("/Users/O'Brian/a $(touch nope) `id` $HOME/config.json", "'/Users/O'\"'\"'Brian/a $(touch nope) `id` $HOME/config.json'")]
    public async Task QuoteShellArgument_ProtectsAllShellExpansions(string input, string expected)
    {
        await CoreAdminManager.QuoteShellArgument(input).Should().BeEqualTo(expected);
    }
}
