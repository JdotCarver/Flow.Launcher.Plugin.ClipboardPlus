// LocalPinProbe.cs
// Read-only local diagnostic: emit fixed status categories, never IDs or data.
using Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;
using Windows.ApplicationModel.DataTransfer;
using System.Text.Json;

internal static class LocalPinProbe
{
    public static async Task RunAsync()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Clipboard", "Pinned");
            var trusted = PinnedHistorySnapshotReader.TryRead(root, out var pinnedIds);
            if (!trusted && HasUtf8Bom(root)) Console.WriteLine("pin_probe_utf8_bom");
            if (!trusted) Console.WriteLine("pin_probe_reason_" + ExplainUntrusted(root));
            var history = await Clipboard.GetHistoryItemsAsync();
            if (history.Status != ClipboardHistoryItemsResultStatus.Success)
            {
                Console.WriteLine("pin_probe_history_unavailable");
                return;
            }
            Console.WriteLine(trusted ? "pin_probe_trusted" : "pin_probe_untrusted");
            Console.WriteLine(history.Items.Count == 0 ? "pin_probe_history_empty" : "pin_probe_history_nonempty");
            Console.WriteLine(history.Items.Any(item => pinnedIds.Contains(item.Id))
                ? "pin_probe_pinned_match" : "pin_probe_no_pinned_match");
        }
        catch (Exception)
        {
            Console.WriteLine("pin_probe_history_unavailable");
        }
    }
    private static bool HasUtf8Bom(string root)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                using var stream = File.OpenRead(Path.Combine(directory, "metadata.json"));
                if (stream.ReadByte() == 0xef && stream.ReadByte() == 0xbb && stream.ReadByte() == 0xbf) return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    private static string ExplainUntrusted(string root)
    {
        try
        {
            _ = File.GetAttributes(root);
            if (Directory.EnumerateFiles(root).Any()) return "root_files";
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                using var stream = File.OpenRead(Path.Combine(directory, "metadata.json"));
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return "root_shape";
                if (!document.RootElement.TryGetProperty("Items", out var items) &&
                    !document.RootElement.TryGetProperty("items", out items)) return "missing_items";
                if (items.ValueKind != JsonValueKind.Object) return "items_shape";
            }
            return "recovered";
        }
        catch (DirectoryNotFoundException) { return "directory_missing"; }
        catch (FileNotFoundException) { return "file_missing"; }
        catch (UnauthorizedAccessException) { return "access"; }
        catch (JsonException) { return "json"; }
        catch (IOException) { return "io"; }
        catch (Exception) { return "other"; }
    }

}
