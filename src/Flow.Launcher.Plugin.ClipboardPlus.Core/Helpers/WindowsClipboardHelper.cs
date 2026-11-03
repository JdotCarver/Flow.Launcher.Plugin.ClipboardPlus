// WindowsClipboardHelper.cs
// Copyright (c) 2025 Jack251970
// Licensed under the Apache License. See the LICENSE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Flow.Launcher.Plugin.ClipboardPlus.Core.Helpers;

#pragma warning disable CA1416 // Validate platform compatibility

/// <summary>
/// https://learn.microsoft.com/en-us/uwp/api/clipboard
/// https://docs.microsoft.com/windows/release-health/release-information
/// </summary>
public class WindowsClipboardHelper : IDisposable
{
    private static string ClassName => nameof(WindowsClipboardHelper);

    #region Helper Operations

    public static bool IsClipboardHistorySupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
    }

    public static bool IsHistoryEnabled()
    {
        if (IsClipboardHistorySupported())
        {
            return Clipboard.IsHistoryEnabled();
        }

        return false;
    }

    public static async Task<int> ClearUnpinnnedRecordsAsync()
    {
        if (IsClipboardHistorySupported())
        {
            var historyItems = await Clipboard.GetHistoryItemsAsync();
            if (Clipboard.ClearHistory() && historyItems.Status == ClipboardHistoryItemsResultStatus.Success)
            {
                var historyItemsAfter = await Clipboard.GetHistoryItemsAsync();
                if (historyItemsAfter.Status == ClipboardHistoryItemsResultStatus.Success)
                {
                    return historyItems.Items.Count - historyItemsAfter.Items.Count;
                }
            }
        }

        return -1;
    }

    public static async Task<int> ClearAllRecordsAsync()
    {
        if (IsClipboardHistorySupported())
        {
            var historyItems = await Clipboard.GetHistoryItemsAsync();
            if (historyItems.Status == ClipboardHistoryItemsResultStatus.Success)
            {
                foreach (var item in historyItems.Items)
                {
                    Clipboard.DeleteItemFromHistory(item);
                }
                return historyItems.Items.Count;
            }
        }

        return -1;
    }

    public static bool DeleteItemFromHistory(ClipboardData clipboardData)
    {
        if (IsClipboardHistorySupported() && clipboardData.ClipboardHistoryItem is ClipboardHistoryItem item)
        {
            return Clipboard.DeleteItemFromHistory(item);
        }

        return false;
    }

    #endregion

    #region Pinned Items Helper

    private volatile bool pinStateTrusted;
    private volatile HashSet<string> knownPinnedItemIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly PinnedHistoryChangeTracker pinChanges = new();

    /// <summary>False means pin state could not be verified and suppression must fail open.</summary>
    public bool IsPinStateTrusted => pinStateTrusted;

    /// <summary>Suppression is safe only while the latest verified snapshot excludes the item.</summary>
    public bool IsKnownUnpinned(string id)
    {
        var snapshot = knownPinnedItemIds;
        return !string.IsNullOrWhiteSpace(id) && pinStateTrusted && !snapshot.Contains(id) &&
            ReferenceEquals(snapshot, knownPinnedItemIds) && pinStateTrusted;
    }

    private HashSet<string> GetPinnedClipboardItemIds(out bool trusted)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Clipboard", "Pinned");
        trusted = PinnedHistorySnapshotReader.TryRead(folder, out var ids);
        // Publish a new, never-mutated set while trust is down. Readers must
        // not combine an old snapshot with a later successful verification.
        pinStateTrusted = false;
        knownPinnedItemIds = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        pinStateTrusted = trusted;
        if (!trusted) pinChanges.Invalidate();
        return ids;
    }

    #endregion

    #region Initialization

    private IClipboardPlus _clipboardPlus = null!;
    private PluginInitContext? _context => _clipboardPlus.Context;

    public void SetClipboardPlus(IClipboardPlus clipboardPlus)
    {
        _clipboardPlus = clipboardPlus;
    }

    #endregion

    #region Enable / Disable

    public void EnableClipboardHistory()
    {
        if (IsClipboardHistorySupported())
        {
            _previousPinnedItemIds = GetPinnedClipboardItemIds(out var trusted);
            pinChanges.Reset(_previousPinnedItemIds, trusted);
            Clipboard_HistoryChanged(this, null!);
            Clipboard.HistoryChanged += Clipboard_HistoryChanged;
            Clipboard.HistoryEnabledChanged += Clipboard_HistoryEnabledChanged;
        }
    }

    public void DisableClipboardHistory()
    {
        if (IsClipboardHistorySupported())
        {
            _clipboardHistoryItems.Clear();
            _clipboardHistoryItemsIds.Clear();
            _previousPinnedItemIds.Clear();
            pinChanges.Invalidate();
            Clipboard.HistoryChanged -= Clipboard_HistoryChanged;
            Clipboard.HistoryEnabledChanged -= Clipboard_HistoryEnabledChanged;
        }
    }

    #endregion

    #region Events

    public event EventHandler<ClipboardData>? OnHistoryItemAdded;
    public event EventHandler<string[]>? OnHistoryItemRemoved;
    public event EventHandler<string[]>? OnHistoryReadCompleted;
    public event EventHandler<(string HashId, bool Pinned)>? OnHistoryItemPinUpdated;

    public event EventHandler<bool>? OnHistoryEnabledChanged;

    private readonly SemaphoreSlim _historyItemLock = new(1, 1);

    private readonly List<string> _clipboardHistoryItemsIds = [];
    private readonly List<ClipboardHistoryItem> _clipboardHistoryItems = [];
    private HashSet<string> _previousPinnedItemIds = [];

    private void Clipboard_HistoryChanged(object? sender, ClipboardHistoryChangedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await _historyItemLock.WaitAsync();
            try
            {
                var historyItems = await Clipboard.GetHistoryItemsAsync();
                if (historyItems.Status == ClipboardHistoryItemsResultStatus.Success)
                {
                    var items = historyItems.Items;

                    // invoke the event
                    if (e != null)
                    {
                        if (_clipboardHistoryItems.Count < items.Count)  // Item added
                        {
                            await AddItemAsync(items);
                        }
                        else if (_clipboardHistoryItems.Count > items.Count)  // Item removed
                        {
                            RemoveItem(items);
                        }
                        else // Windows clipboard history is full, or item pin updated
                        {
                            if (!await UpdatedPinnedItemsAsync(items))
                            {
                                RemoveItem(items);
                                await AddItemAsync(items);
                            }
                        }
                    }

                    // refresh the list
                    _clipboardHistoryItems.Clear();
                    _clipboardHistoryItemsIds.Clear();
                    foreach (var item in items)
                    {
                        _clipboardHistoryItems.Add(item);
                        _clipboardHistoryItemsIds.Add(GetHashId(item));
                    }
                    // Only a successful complete raw Windows read can prune IDs.
                    OnHistoryReadCompleted?.Invoke(this, _clipboardHistoryItemsIds.ToArray());
                }
            }
            finally
            {
                _historyItemLock.Release();
            }

            void RemoveItem(IReadOnlyList<ClipboardHistoryItem> items)
            {
                var newClipboardHistoryItemsIds = items.Select(x => GetHashId(x)).ToList();
                var removedItems = _clipboardHistoryItems
                    .Where(x => !newClipboardHistoryItemsIds.Contains(GetHashId(x)))
                    .Select(x => GetHashId(x))
                    .ToArray();
                if (removedItems.Length == 0) return;
                OnHistoryItemRemoved?.Invoke(this, removedItems);
                _context.LogDebug(ClassName, $"Clipboard_HistoryChanged: Removed {removedItems.Length} items");
            }

            async Task AddItemAsync(IReadOnlyList<ClipboardHistoryItem> items)
            {
                var newItem = items.FirstOrDefault(x => !_clipboardHistoryItemsIds.Contains(GetHashId(x)));
                if (newItem == null) return;
                _previousPinnedItemIds = GetPinnedClipboardItemIds(out var trusted);
                pinChanges.Reset(_previousPinnedItemIds, trusted);
                var addedItem = await GetClipboardData(newItem, _previousPinnedItemIds, trusted);
                if (!addedItem.IsNull())
                {
                    OnHistoryItemAdded?.Invoke(this, addedItem);
                    _context.LogDebug(ClassName, "Clipboard_HistoryChanged: Added item");
                }
                else
                {
                    _context.LogDebug(ClassName, "Clipboard_HistoryChanged: Failed to get clipboard data for added item");
                }
            }

            async Task<bool> UpdatedPinnedItemsAsync(IReadOnlyList<ClipboardHistoryItem> items)
            {
                var currentPinnedIds = GetPinnedClipboardItemIds(out var trusted);
                var changes = pinChanges.Observe(items.Select(GetHashId), currentPinnedIds, trusted);
                _previousPinnedItemIds = currentPinnedIds;
                foreach (var (id, pinned) in changes)
                {
                    OnHistoryItemPinUpdated?.Invoke(this, (id, pinned));
                }
                return changes.Count > 0;
            }
        });
    }

    private async void Clipboard_HistoryEnabledChanged(object? sender, object e)
    {
        if (IsHistoryEnabled())
        {
            // Update previous pinned IDs under the history item lock
            var currentPinnedIds = GetPinnedClipboardItemIds(out var trusted);
            await _historyItemLock.WaitAsync();
            try
            {
                _previousPinnedItemIds = currentPinnedIds;
                pinChanges.Reset(currentPinnedIds, trusted);
            }
            finally
            {
                _historyItemLock.Release();
            }

            // Trigger history changed handling (this method uses _historyItemLock internally)
            Clipboard_HistoryChanged(this, null!);
        }
        else
        {
            // Clear history collections and previous pinned IDs under the history item lock
            await _historyItemLock.WaitAsync();
            try
            {
                _clipboardHistoryItems.Clear();
                _clipboardHistoryItemsIds.Clear();
                _previousPinnedItemIds.Clear();
                pinChanges.Invalidate();
            }
            finally
            {
                _historyItemLock.Release();
            }
        }
        OnHistoryEnabledChanged?.Invoke(this, IsHistoryEnabled());
    }

    #endregion

    #region History Items

    public async Task<List<ClipboardData>?> GetHistoryItemsAsync() =>
        (await GetHistoryItemsWithPinStateAsync()).Items;

    public async Task<List<ClipboardData>?> GetLaterHistoryItemsAsync(DateTime dateTime) =>
        (await GetLaterHistoryItemsWithPinStateAsync(dateTime)).Items;

    public async Task<(List<ClipboardData>? Items, bool PinStateTrusted)> GetHistoryItemsWithPinStateAsync()
    {
        if (IsClipboardHistorySupported())
        {
            await _historyItemLock.WaitAsync();
            try
            {
                // filter and sort the items (later item last)
                var laterSortedItems = _clipboardHistoryItems
                    .OrderBy(x => x.Timestamp.DateTime)
                    .ToList();

                // get the clipboard data
                var clipboardDataItems = new List<ClipboardData>();
                var currentPinnedIds = GetPinnedClipboardItemIds(out var trusted);
                foreach (var item in laterSortedItems)
                {
                    var clipboardData = await GetClipboardData(item, currentPinnedIds, trusted);
                    if (!clipboardData.IsNull())
                    {
                        clipboardDataItems.Add(clipboardData);
                        _clipboardPlus.ScoreHelper.Add();
                    }
                }
                return (clipboardDataItems, trusted);
            }
            finally
            {
                _historyItemLock.Release();
            }
        }

        return (null, false);
    }

    public async Task<(List<ClipboardData>? Items, bool PinStateTrusted)> GetLaterHistoryItemsWithPinStateAsync(DateTime dateTime)
    {
        if (IsClipboardHistorySupported())
        {
            await _historyItemLock.WaitAsync();
            try
            {
                // filter and sort the items (later item last)
                var laterSortedItems = _clipboardHistoryItems
                    .Where(x => x.Timestamp.DateTime > dateTime)
                    .OrderBy(x => x.Timestamp.DateTime)
                    .ToList();

                // get the clipboard data
                var clipboardDataItems = new List<ClipboardData>();
                var currentPinnedIds = GetPinnedClipboardItemIds(out var trusted);
                foreach (var item in laterSortedItems)
                {
                    var clipboardData = await GetClipboardData(item, currentPinnedIds, trusted);
                    if (!clipboardData.IsNull())
                    {
                        clipboardDataItems.Add(clipboardData);
                        _clipboardPlus.ScoreHelper.Add();
                    }
                }
                return (clipboardDataItems, trusted);
            }
            finally
            {
                _historyItemLock.Release();
            }
        }

        return (null, false);
    }

    private async Task<ClipboardData> GetClipboardData(ClipboardHistoryItem item, HashSet<string> currentPinnedIds, bool trusted)
    {
        // If the clipboard is empty, return.
        var dataObj = item.Content;
        if (dataObj == null)
        {
            return ClipboardData.NULL;
        }

        // Get hash id & create time & pinned list
        var hashId = GetHashId(item);
        var createTime = item.Timestamp.DateTime;
        // Unknown pin state is treated as pinned, so it cannot be hidden.
        var pinned = !trusted || currentPinnedIds.Contains(hashId);

        // Determines whether a file/files have been cut/copied.
        if (_clipboardPlus.ObservableDataFormats.Images && ClipboardHandleWin.IsDataImage(dataObj))
        {
            // Make sure on the application dispatcher.
            var clipboardData = await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (await ClipboardHandleWin.GetImageContentAsync(dataObj) is BitmapImage capturedImage)
                    {
                        var clipboardData = _clipboardPlus.GetClipboardDataItem(
                            capturedImage,
                            DataType.Image,
                            hashId,
                            createTime,
                            SourceApplication.NULL,
                            string.Empty,
                            string.Empty
                        );
                        if (!clipboardData.IsNull())
                        {
                            clipboardData.ClipboardHistoryItem = item;
                            clipboardData.Pinned = pinned;
                        }
                        return clipboardData;
                    }
                }
                catch (Exception)
                {
                    // Ignored
                }

                return ClipboardData.NULL;
            });
            return await clipboardData;
        }
        // Determines whether plain text or rich text has been cut/copied.
        else if (_clipboardPlus.ObservableDataFormats.Texts && ClipboardHandleWin.IsDataText(dataObj))
        {
            var (plainText, richText, dataType) = await ClipboardHandleWin.GetTextContentAsync(dataObj);
            var clipboardData = _clipboardPlus.GetClipboardDataItem(
                dataType == DataType.PlainText ? plainText : richText,
                dataType,
                hashId,
                createTime,
                SourceApplication.NULL,
                plainText,
                richText
            );
            if (!clipboardData.IsNull())
            {
                clipboardData.ClipboardHistoryItem = item;
                clipboardData.Pinned = pinned;
            }
            return clipboardData;
        }
        // Determines whether a file has been cut/copied.
        else if (_clipboardPlus.ObservableDataFormats.Files && ClipboardHandleWin.IsDataFiles(dataObj))
        {
            // If the 'capturedFiles' string array persists as null, then this means
            // that the copied content is of a complex object type since the file-drop
            // format is able to capture more-than-just-file content in the clipboard.
            // Therefore assign the content its rightful type.
            if (await ClipboardHandleWin.GetFilesContentAsync(dataObj) is string[] capturedFiles)
            {
                var clipboardData = _clipboardPlus.GetClipboardDataItem(
                    capturedFiles,
                    DataType.Files,
                    hashId,
                    createTime,
                    SourceApplication.NULL,
                    string.Empty,
                    string.Empty
                );
                if (!clipboardData.IsNull())
                {
                    clipboardData.ClipboardHistoryItem = item;
                    clipboardData.Pinned = pinned;
                }
                return clipboardData;
            }
            else
            {
                var clipboardData = _clipboardPlus.GetClipboardDataItem(
                    dataObj,
                    DataType.Other,
                    hashId,
                    createTime,
                    SourceApplication.NULL,
                    string.Empty,
                    string.Empty
                );
                if (!clipboardData.IsNull())
                {
                    clipboardData.ClipboardHistoryItem = item;
                    clipboardData.Pinned = pinned;
                }
                return clipboardData;
            }
        }
        // Determines whether an unknown object has been cut/copied.
        else if (_clipboardPlus.ObservableDataFormats.Others && (!ClipboardHandleWin.IsDataFiles(dataObj)))
        {
            var clipboardData = _clipboardPlus.GetClipboardDataItem(
                dataObj,
                DataType.Other,
                hashId,
                createTime,
                SourceApplication.NULL,
                string.Empty,
                string.Empty
            );
            if (!clipboardData.IsNull())
            {
                clipboardData.ClipboardHistoryItem = item;
                clipboardData.Pinned = pinned;
            }
            return clipboardData;
        }

        return ClipboardData.NULL;
    }

    // item.Id with numbers and capital letters, while StringUtils.GetGuid() with numbers and lowercase letters
    // Therefore, we need to convert the item.Id to lowercase.
    private static string GetHashId(ClipboardHistoryItem item)
    {
        return item.Id.ToLower();
    }

    #endregion

    #region IDisposable

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisableClipboardHistory();
            OnHistoryItemAdded = null;
            OnHistoryItemRemoved = null;
            OnHistoryReadCompleted = null;
            OnHistoryItemPinUpdated = null;
            _historyItemLock.Dispose();
            _disposed = true;
        }
    }

    #endregion
}
