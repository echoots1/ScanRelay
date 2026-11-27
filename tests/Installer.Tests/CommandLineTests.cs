namespace ScanRelay.Installer.Tests;

public class CommandLineTests
{
    [Fact]
    public void No_arguments_opens_the_wizard()
    {
        var line = CommandLine.Parse([]);
        Assert.Equal(SetupMode.Auto, line.Mode);
        Assert.False(line.Quiet);
        Assert.Null(line.AnswersFile);
        Assert.False(line.KeepData);
        Assert.Null(line.Error);
    }

    [Theory]
    [InlineData("/modify", SetupMode.Modify)]
    [InlineData("/MODIFY", SetupMode.Modify)]
    [InlineData("--modify", SetupMode.Modify)]
    [InlineData("/uninstall", SetupMode.Uninstall)]
    [InlineData("-uninstall", SetupMode.Uninstall)]
    [InlineData("/modules", SetupMode.Modules)]
    public void A_mode_opens_the_matching_screen(string arg, SetupMode expected)
    {
        var line = CommandLine.Parse([arg]);
        Assert.Equal(expected, line.Mode);
        Assert.False(line.Quiet);
        Assert.Null(line.Error);
    }

    [Fact]
    public void Quiet_install_takes_an_answers_file()
    {
        var line = CommandLine.Parse(["/quiet", "/answers", @"C:\Temp\answers.json"]);
        Assert.True(line.Quiet);
        Assert.Equal(SetupMode.Auto, line.Mode);
        Assert.Equal(@"C:\Temp\answers.json", line.AnswersFile);
        Assert.Null(line.Error);

        Assert.Equal(line, CommandLine.Parse(["/answers", @"C:\Temp\answers.json", "/QUIET"]));
    }

    [Fact]
    public void Quiet_alone_uses_defaults_or_the_saved_answers()
    {
        var line = CommandLine.Parse(["/quiet"]);
        Assert.True(line.Quiet);
        Assert.Null(line.AnswersFile);
        Assert.Null(line.Error);
    }

    [Fact]
    public void Quiet_uninstall_may_keep_the_data()
    {
        var remove = CommandLine.Parse(["/quiet", "/uninstall"]);
        Assert.True(remove.Quiet);
        Assert.Equal(SetupMode.Uninstall, remove.Mode);
        Assert.False(remove.KeepData);
        Assert.Null(remove.Error);

        var keep = CommandLine.Parse(["/quiet", "/uninstall", "/keepdata"]);
        Assert.True(keep.KeepData);
        Assert.Null(keep.Error);
    }

    [Theory]
    [InlineData("/answers")]
    [InlineData("/quiet", "/answers")]
    [InlineData("/answers", "file.json")]
    [InlineData("/keepdata")]
    [InlineData("/quiet", "/keepdata")]
    [InlineData("/quiet", "/uninstall", "/answers", "file.json")]
    [InlineData("/modify", "/uninstall")]
    [InlineData("/quiet", "/modify")]
    [InlineData("/frobnicate")]
    [InlineData("stray")]
    public void Mistakes_are_reported_not_guessed_at(params string[] args)
    {
        Assert.NotNull(CommandLine.Parse(args).Error);
    }

    [Fact]
    public void Quiet_is_honoured_even_when_something_else_is_wrong()
    {
        // Otherwise an automated run with a typo would sit behind a message box nobody can see.
        var line = CommandLine.Parse(["/frobnicate", "/quiet"]);
        Assert.True(line.Quiet);
        Assert.Contains("/frobnicate", line.Error);
    }

    [Fact]
    public void Help_is_recognised()
    {
        Assert.True(CommandLine.Parse(["/?"]).Help);
        Assert.Contains("/quiet /answers", CommandLine.Usage);
    }

    [Theory]
    [InlineData("/modules", "/quiet")]
    [InlineData("/modules", "/uninstall")]
    [InlineData("/modify", "/modules")]
    public void The_modules_step_opens_a_window_and_goes_with_nothing_else(string first, string second) =>
        Assert.NotNull(CommandLine.Parse([first, second]).Error);

    [Theory]
    [InlineData("/tray", SetupMode.Tray)]
    [InlineData("/open", SetupMode.Open)]
    [InlineData("/STATUS", SetupMode.Status)]
    public void The_everyday_modes_need_no_administrator_to_begin_with(string arg, SetupMode expected)
    {
        var line = CommandLine.Parse([arg]);

        Assert.Equal(expected, line.Mode);
        Assert.True(line.IsEverydayUse);
        Assert.Null(line.Error);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("Restart")]
    public void The_service_can_be_started_stopped_or_restarted(string verb)
    {
        var line = CommandLine.Parse(["/service", verb]);

        Assert.Equal(SetupMode.Service, line.Mode);
        Assert.Equal(verb.ToLowerInvariant(), line.ServiceVerb);
        Assert.Null(line.Error);
    }

    [Theory]
    [InlineData("/service")]
    [InlineData("/service", "delete")]
    [InlineData("/open", "/quiet")]
    [InlineData("/tray", "/uninstall")]
    [InlineData("/modify", "/open")]
    [InlineData("/status", "/answers", "a.json")]
    public void An_everyday_mode_goes_with_nothing_else_and_a_service_needs_a_known_verb(params string[] args) =>
        Assert.NotNull(CommandLine.Parse(args).Error);

    [Fact]
    public void Setup_modes_are_not_everyday_use() =>
        Assert.All(new[] { "/modify", "/uninstall", "/modules", "/quiet" }, a => Assert.False(CommandLine.Parse([a]).IsEverydayUse));
}
