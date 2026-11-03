// LocalPinProbe.cs
// Read-only local diagnostic: emit fixed status categories, never IDs or data.
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;
using Windows.ApplicationModel.DataTransfer;

internal static class LocalPinProbe
{
    public static async Task RunAsync()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Clipboard", "Pinned");
            var trusted = PinnedHistorySnapshotReader.TryRead(root, out var pinnedIds);
            var history = await Clipboard.GetHistoryItemsAsync();
            if (history.Status != ClipboardHistoryItemsResultStatus.Success)
            {
                Console.WriteLine("pin_probe_history_unavailable");
                return;
            }
            Console.WriteLine(trusted ? "pin_probe_trusted" : "pin_probe_untrusted");
            Console.WriteLine(history.Items.Count == 0 ? "pin_probe_history_empty" : "pin_probe_history_nonempty");
            if (trusted)
                Console.WriteLine(history.Items.Any(item => pinnedIds.Contains(item.Id))
                    ? "pin_probe_pinned_match" : "pin_probe_no_pinned_match");
        }
        catch (Exception)
        {
            Console.WriteLine("pin_probe_history_unavailable");
        }
    }
}
