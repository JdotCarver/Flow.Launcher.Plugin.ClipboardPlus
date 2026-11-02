// SettingsStub.cs
// The upstream matcher's unused Settings-taking constructor needs this type.
// The harness uses its alphabet-only constructor and explicitly sets precision.
namespace Flow.Launcher.Infrastructure.UserSettings;

public sealed class Settings
{
    public Flow.Launcher.Plugin.SharedModels.SearchPrecisionScore QuerySearchPrecision { get; set; }
}
