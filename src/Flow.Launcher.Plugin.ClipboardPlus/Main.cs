// Main.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WindowsInput;
using Clipboard = System.Windows.Clipboard;

namespace Flow.Launcher.Plugin.ClipboardPlus;

public partial class ClipboardPlus : IAsyncPlugin, IAsyncReloadable, IContextMenu, IPluginI18n,
    IResultUpdated, ISavable, ISettingProvider, IClipboardPlus, IAsyncDisposable
{
    #region Fields

    // Class name for logging
    private static readonly string ClassName = nameof(ClipboardPlus);

    // Plugin context
    private PluginInitContext? Context = null;

    // State of using Windows clipboard history only
    private bool UseWindowsClipboardHistoryOnly;

    // Culture info
    private CultureInfo CultureInfo = null!;

    // Settings
    private Settings Settings = null!;

    // Score helper
    private ScoreHelper ScoreHelper = null!;

    // Database helper
    private SqliteDatabase Database = null!;

    // Clipboard monitor instance
    // Warning: Do not init the instance in InitAsync function! This will cause issues.
    private IClipboardMonitor? ClipboardMonitor;

    // Observable data formats
    private readonly ObservableDataFormats ObservableDataFormats = new()
    {
        Images = true,
        Texts = true,
        Files = true,
        Others = false
    };

    // Windows clipboard helper
    private readonly WindowsClipboardHelper WindowsClipboardHelper = new();

    // Records list & Score
    // Latest records are at the beginning of the list.
    private LinkedList<ClipboardDataPair> RecordsList = new();

    // Record list lock
    private readonly SemaphoreSlim RecordsLock = new(1, 1);

    // Score interval
    // Note: Get scores of the items further apart to make sure the ranking seqence of items is correct.
    // https://github.com/Flow-Launcher/Flow.Launcher/issues/2904
    private const int ScoreInterval = 10000;

    // Clipboard retry times
    private const int ClipboardRetryTimes = 5;

    // Retry interval
    private const int RetryInterval = 100;

    // Max length of string
    private const int StringMaxLength = 54;

    // Empty results for cancellation
    private readonly List<Result> EmptyResults = [];

    // Minimal count for concurrent operations
    private const int MinConcurrentCount = 333;

    // Maximum characters for result title & title tooltip
    private const int MaxTitleToolTipCharacters = 99;

    #region Scores

    private const int ScoreInterval1 = 1 * ScoreInterval;
    private const int ScoreInterval2 = 2 * ScoreInterval;
    private const int ScoreInterval3 = 3 * ScoreInterval;
    private const int ScoreInterval4 = 4 * ScoreInterval;
    private const int ScoreInterval5 = 5 * ScoreInterval;
    private const int ScoreInterval6 = 6 * ScoreInterval;
    private const int ScoreInterval7 = 7 * ScoreInterval;
    private const int ScoreInterval8 = 8 * ScoreInterval;
    private const int ScoreInterval9 = 9 * ScoreInterval;
    private const int ScoreInterval10 = 10 * ScoreInterval;
    private const int ScoreInterval11 = 11 * ScoreInterval;
    private const int ScoreInterval12 = 12 * ScoreInterval;
    private const int ScoreInterval13 = 13 * ScoreInterval;
    private const int ScoreInterval14 = 14 * ScoreInterval;

    private const int TopActionScore1 = 2 * ClipboardData.MaximumScore + 4 * ScoreInterval;
    private const int TopActionScore2 = 2 * ClipboardData.MaximumScore + 3 * ScoreInterval;
    private const int TopActionScore3 = 2 * ClipboardData.MaximumScore + 2 * ScoreInterval;
    private const int TopActionScore4 = 2 * ClipboardData.MaximumScore + 1 * ScoreInterval;

    private const int BottomActionScore1 = 8000;
    private const int BottomActionScore2 = 6000;
    private const int BottomActionScore3 = 4000;
    private const int BottomActionScore4 = 2000;

    #endregion

    #endregion

    #region Constructor

    public ClipboardPlus()
    {
        cleanupCoordinator = new CleanupRunCoordinator(RunCleanupWorkAsync);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            ClipboardMonitor = new ClipboardMonitorWin()
            {
                ObserveLastEntry = false,
                ObservableFormats = ObservableDataFormats
            };
        }
        else
        {
            ClipboardMonitor = new ClipboardMonitorW()
            {
                ObserveLastEntry = false,
                ObservableFormats = ObservableDataFormats
            };
        }
    }

    #endregion

    #region IAsyncPlugin Interface

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        try
        {
            return await Task.Run(() => Query(query, token), token);
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        return EmptyResults;
    }

    public async Task<List<Result>> Query(Query query, CancellationToken token)
    {
        var results = new List<Result>();
        var queryGeneration = Interlocked.Increment(ref cleanupQueryGeneration);
        if (query.FirstSearch == Settings.ClearKeyword)
        {
            // clean windows clipboard history actions
            token.ThrowIfCancellationRequested();
            if (WindowsClipboardHelper.IsHistoryEnabled())
            {
                results.AddRange(
                    [
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_clear_all_system_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_clear_all_system_subtitle(),
                            IcoPath = PathHelper.AppIconPath,
                            Glyph = ResourceHelper.ClearHistoryGlyph,
                            Score = ScoreInterval6,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                _ = Task.Run(async () =>
                                {
                                    var number = await WindowsClipboardHelper.ClearAllRecordsAsync();
                                    if (number > 0)
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_all_system_msg_subtitle(number));
                                    }
                                    else if (number == 0)
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_fail_msg_subtitle());
                                    }
                                    else
                                    {
                                        Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_fail_clear_system_msg_subtitle());
                                    }
                                });
                                return true;
                            }
                        },
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_clear_unpin_system_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_clear_unpin_system_subtitle(),
                            IcoPath = PathHelper.AppIconPath,
                            Glyph = ResourceHelper.ClearHistoryGlyph,
                            Score = ScoreInterval5,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                _ = Task.Run(async () =>
                                {
                                    var number = await WindowsClipboardHelper.ClearUnpinnnedRecordsAsync();
                                    if (number > 0)
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_unpin_system_msg_subtitle(number));
                                    }
                                    else if (number == 0)
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_fail_msg_subtitle());
                                    }
                                    else
                                    {
                                        Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_fail_clear_system_msg_subtitle());
                                    }
                                });
                                return true;
                            },
                        }
                    ]);
            }

            // clear list and database actions
            token.ThrowIfCancellationRequested();
            results.Add(new Result
            {
                Title = Localize.flowlauncher_plugin_clipboardplus_clear_list_title(),
                SubTitle = Localize.flowlauncher_plugin_clipboardplus_clear_list_subtitle(),
                IcoPath = PathHelper.ListIconPath,
                Glyph = ResourceHelper.ListGlyph,
                Score = ScoreInterval4,
                AddSelectedCount = false,
                Action = (c) =>
                {
                    _ = Task.Run(async () =>
                    {
                        var number = await DeleteAllRecordsFromListAsync();
                        if (number > 0)
                        {
                            Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                Localize.flowlauncher_plugin_clipboardplus_clear_list_msg_subtitle(number));
                        }
                        else
                        {
                            Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                Localize.flowlauncher_plugin_clipboardplus_clear_fail_msg_subtitle());
                        }
                    });
                    return true;
                },
            });
            if (!UseWindowsClipboardHistoryOnly)
            {
                results.AddRange(
                    [
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_clear_both_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_clear_both_subtitle(),
                            IcoPath = PathHelper.DatabaseIconPath,
                            Glyph = ResourceHelper.DatabaseGlyph,
                            Score = ScoreInterval3,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                _ = Task.Run(async () =>
                                {
                                    var number = await DeleteAllRecordsFromListDatabaseAsync();
                                    if (number > 0)
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_both_msg_subtitle(number));
                                    }
                                    else
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_fail_msg_subtitle());
                                    }
                                });
                                return true;
                            }
                        },
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_clear_unpin_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_clear_unpin_subtitle(),
                            IcoPath = PathHelper.UnpinIcon1Path,
                            Glyph = ResourceHelper.UnpinGlyph,
                            Score = ScoreInterval2,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                _ = Task.Run(async () =>
                                {
                                    var number = await DeleteUnpinnedRecordsFromListDatabaseAsync();
                                    if (number > 0)
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_unpin_msg_subtitle(number));
                                    }
                                    else
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_fail_msg_subtitle());
                                    }
                                });
                                return true;
                            }
                        },
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_clear_invalid_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_clear_invalid_subtitle(),
                            IcoPath = PathHelper.ErrorIconPath,
                            Glyph = ResourceHelper.ErrorGlyph,
                            Score = ScoreInterval1,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                _ = Task.Run(async () =>
                                {
                                    var number = await DeleteInvalidRecordsFromListDatabaseAsync();
                                    if (number > 0)
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_invalid_msg_subtitle(number));
                                    }
                                    else
                                    {
                                        Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                                            Localize.flowlauncher_plugin_clipboardplus_clear_fail_msg_subtitle());
                                    }
                                });
                                return true;
                            }
                        }
                    ]
                );
            }
            results.Add(CreateCleanupAction(results, query, token, queryGeneration));
        }
        else
        {
            var querySearch = query.Search;
            if (string.IsNullOrWhiteSpace(querySearch))
            {
                // clean action
                token.ThrowIfCancellationRequested();
                results.Add(new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_clean_title(),
                    SubTitle = Localize.flowlauncher_plugin_clipboardplus_clean_subtitle(),
                    IcoPath = PathHelper.CleanIconPath,
                    Glyph = ResourceHelper.CleanGlyph,
                    Score = Settings.ActionTop ? TopActionScore1 : BottomActionScore1,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        _ = Win32Helper.StartSTATaskAsync(Clipboard.Clear);
                        ClipboardMonitor?.CleanClipboard();
                        return true;
                    },
                });

                // connect & disconnect action
                token.ThrowIfCancellationRequested();
                if (ClipboardMonitor != null)
                {
                    if (ClipboardMonitor.MonitorClipboard)
                    {
                        results.Add(new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_disconnect_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_disconnect_subtitle(),
                            IcoPath = PathHelper.DisconnectIconPath,
                            Glyph = ResourceHelper.DisconnectGlyph,
                            Score = Settings.ActionTop ? TopActionScore3 : BottomActionScore3,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                ClipboardMonitor.PauseMonitoring();
                                return true;
                            },
                        });
                    }
                    else
                    {
                        results.Add(new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_connect_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_connect_subtitle(),
                            IcoPath = PathHelper.ConnectIconPath,
                            Glyph = ResourceHelper.ConnectGlyph,
                            Score = Settings.ActionTop ? TopActionScore3 : BottomActionScore3,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                ClipboardMonitor.ResumeMonitoring();
                                return true;
                            },
                        });
                    }
                }

                // clear action
                token.ThrowIfCancellationRequested();
                results.Add(new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_clear_title(),
                    SubTitle = Localize.flowlauncher_plugin_clipboardplus_clear_subtitle(),
                    IcoPath = PathHelper.ClearIconPath,
                    Glyph = ResourceHelper.ClearGlyph,
                    Score = Settings.ActionTop ? TopActionScore4 : BottomActionScore4,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        Context!.API.ChangeQuery($"{query.ActionKeyword}{Plugin.Query.TermSeparator}{Settings.ClearKeyword} ", true);
                        return false;
                    },
                });

                // update results
                token.ThrowIfCancellationRequested();
                ResultsUpdated?.Invoke(this, new ResultUpdatedEventArgs
                {
                    Results = results,
                    Query = query
                });
            }

            Context.LogDebug(ClassName, $"Wait for record lock");

            await RecordsLock.WaitAsync(token);

            Context.LogDebug(ClassName, $"Start to add results");

            try
            {
                if (string.IsNullOrWhiteSpace(querySearch))
                {
                    // just add full query list
                    foreach (var record in RecordsList)
                    {
                        token.ThrowIfCancellationRequested();
                        var result = GetResultFromClipboardData(record, null);
                        if (result != null)
                        {
                            results.Add(result);
                        }
                    }

                    Context.LogDebug(ClassName, $"Added {RecordsList.Count} records successfully");
                }
                else
                {
                    if (RecordsList.Count > MinConcurrentCount)
                    {
                        // search query list by user input (parallel version)
                        var concurrentResults = new ConcurrentBag<Result>();

                        Parallel.ForEach(RecordsList, (record, state) =>
                        {
                            token.ThrowIfCancellationRequested();
                            var result = GetResultFromClipboardData(record, querySearch);
                            if (result != null)
                            {
                                concurrentResults.Add(result);
                            }
                        });

                        // Add the results from the concurrent bag to the original list
                        results.AddRange(concurrentResults);
                    }
                    else
                    {
                        // search query list by user input
                        foreach (var record in RecordsList)
                        {
                            token.ThrowIfCancellationRequested();
                            var result = GetResultFromClipboardData(record, querySearch);
                            if (result != null)
                            {
                                results.Add(result);
                            }
                        }
                    }

                    Context.LogDebug(ClassName, $"Search added {results.Count} records successfully");
                }
                // Publish the view before releasing the history lock so a
                // concurrent cleanup can never miss the just-built result list.
                if (Interlocked.Read(ref cleanupQueryGeneration) == queryGeneration && !token.IsCancellationRequested)
                    Volatile.Write(ref currentResultView, new CleanupResultView(results, query, token, queryGeneration));
            }
            finally
            {
                RecordsLock.Release();
            }
        }
        if (Interlocked.Read(ref cleanupQueryGeneration) == queryGeneration && !token.IsCancellationRequested)
            Volatile.Write(ref currentResultView, new CleanupResultView(results, query, token, queryGeneration));
        return results;
    }

    public async Task InitAsync(PluginInitContext context)
    {
        Context = context;
        ClipboardMonitor!.SetContext(context);
        WindowsClipboardHelper.SetClipboardPlus(this);

        // init path helper
        PathHelper.Init(context, GetType().Assembly.GetName().Name ?? "Flow.Launcher.Plugin.ClipboardPlus");

        // init settings
        Settings = context.API.LoadSettingJsonStorage<Settings>();
        InitializeWindowsSuppression();
        Context.LogDebug(ClassName, $"Init: {Settings}");

        // init encrypt key
        StringUtils.InitEncryptKey(Settings.EncryptKey);

        // init score helper
        ScoreHelper = new ScoreHelper(ScoreInterval);

        // setup use Windows clipboard history only
        UseWindowsClipboardHistoryOnly = Settings.UseWindowsClipboardHistoryOnly && CheckUseWindowsClipboardHistoryOnly();
        Context.LogInfo(ClassName, $"Use Windows clipboard history only: {UseWindowsClipboardHistoryOnly}");

        if (UseWindowsClipboardHistoryOnly)
        {
            // init database
            Database = new SqliteDatabase(PathHelper.DatabasePath, this);
            await Database.InitializeDatabaseAsync();
            Context.LogDebug(ClassName, "Init database successfully");

            // dispose clipboard monitor
            ClipboardMonitor.ClipboardChanged -= ClipboardMonitor_OnClipboardChanged;
            ClipboardMonitor.Dispose();
            ClipboardMonitor = null;

            // init Windows clipboard helper & records from Windows clipboard history
            EnableWindowsClipboardHelper(true);
        }
        else
        {
            // init database
            var fileExists = File.Exists(PathHelper.DatabasePath);
            Database = new SqliteDatabase(PathHelper.DatabasePath, this);
            await Database.InitializeDatabaseAsync();
            Context.LogDebug(ClassName, "Init database successfully");

            // init records from database
            await InitRecordsFromDatabaseAndSystemAsync(fileExists, false);
            Context.LogDebug(ClassName, $"Init {RecordsList.Count} records successfully");

            // init & start clipboard monitor
            ClipboardMonitor.ClipboardChanged += ClipboardMonitor_OnClipboardChanged;
            ClipboardMonitor.StartMonitoring();
            if (ClipboardMonitor.GetType() == typeof(ClipboardMonitorWin))
            {
                Context.LogInfo(ClassName, "Init Windows clipboard monitor successfully");
            }
            else
            {
                Context.LogInfo(ClassName, "Init WPF clipboard monitor successfully");
            }

            // init Windows clipboard helper & records from Windows clipboard history
            if (Settings.SyncWindowsClipboardHistory)
            {
                EnableWindowsClipboardHelper(true);
            }
        }

        RefreshAutomaticCleanup();

        // init excluded apps
        if (ClipboardMonitor != null && Settings.ExcludedApps != null && Settings.ExcludedApps.Count > 0)
        {
            foreach (var app in Settings.ExcludedApps)
            {
                ClipboardMonitor.AddExcludedPath(app.Path);
            }
            Context.LogInfo(ClassName, $"Init {Settings.ExcludedApps.Count} excluded apps successfully");
        }
    }

    #endregion

    #region IAsyncReloadable Interface

    public async Task ReloadDataAsync()
    {
        // reload records
        await InitRecordsFromDatabaseAndSystemAsync(!UseWindowsClipboardHistoryOnly, true);
    }

    #endregion

    #region IContextMenu Interface

    public List<Result> LoadContextMenus(Result result)
    {
        var results = new List<Result>();
        if (result.ContextData is not ClipboardDataPair clipboardDataPair)
        {
            return results;
        }

        var clipboardData = clipboardDataPair.ClipboardData;
        var dataType = clipboardData.DataType;
        var saved = clipboardData.Saved;
        var pinned = clipboardData.Pinned;
        var fromSystem = clipboardData.FromWindowsClipboardHistory();

        // Copy Default Option
        results.AddRange(
            [
                new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_copy_title(),
                    SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_subtitle(),
                    IcoPath = PathHelper.CopyIconPath,
                    Glyph = ResourceHelper.CopyGlyph,
                    Score = ScoreInterval14,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        CopyToClipboard(clipboardDataPair);
                        return true;
                    }
                }
            ]
        );

        // Copy Addition Options
        switch (dataType)
        {
            case DataType.PlainText:
                results.Add(new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_copy_plain_text_title(),
                    SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_plain_text_subtitle(),
                    IcoPath = PathHelper.CopyIconPath,
                    Glyph = ResourceHelper.CopyGlyph,
                    Score = ScoreInterval8,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        CopyOriginallyToClipboard(clipboardDataPair);
                        return true;
                    }
                });
                break;
            case DataType.RichText:
                results.AddRange(
                    [
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_copy_rich_text_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_rich_text_subtitle(),
                            IcoPath = PathHelper.CopyIconPath,
                            Glyph = ResourceHelper.CopyGlyph,
                            Score = ScoreInterval8,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                CopyOriginallyToClipboard(clipboardDataPair);
                                return true;
                            }
                        },
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_copy_plain_text_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_plain_text_subtitle(),
                            IcoPath = PathHelper.CopyIconPath,
                            Glyph = ResourceHelper.CopyGlyph,
                            Score = ScoreInterval7,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                CopyAsPlainTextToClipboard(clipboardDataPair);
                                return true;
                            }
                        }
                    ]
                );
                break;
            case DataType.Image:
                results.AddRange(
                    [
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_copy_image_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_image_subtitle(),
                            IcoPath = PathHelper.CopyIconPath,
                            Glyph = ResourceHelper.CopyGlyph,
                            Score = ScoreInterval8,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                CopyOriginallyToClipboard(clipboardDataPair);
                                return true;
                            }
                        },
                        new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_copy_image_file_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_image_file_subtitle(),
                            IcoPath = PathHelper.CopyIconPath,
                            Glyph = ResourceHelper.CopyGlyph,
                            Score = ScoreInterval7,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                CopyImageFileToClipboard(clipboardDataPair);
                                return true;
                            }
                        },
                    ]
                );
                break;
            case DataType.Files:
                // Copy as files
                results.Add(new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_copy_files_title(),
                    SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_files_subtitle(),
                    IcoPath = PathHelper.CopyIconPath,
                    Glyph = ResourceHelper.CopyGlyph,
                    Score = ScoreInterval13,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        CopyOriginallyToClipboard(clipboardDataPair);
                        return true;
                    }
                });

                if (clipboardData.Data is string[] filePaths)
                {
                    var existingFilePaths = filePaths.Where(FileUtils.Exists).ToArray();

                    // Copy file path
                    results.Add(new Result
                    {
                        Title = Localize.flowlauncher_plugin_clipboardplus_copy_file_path_title(),
                        SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_file_path_subtitle(),
                        IcoPath = PathHelper.CopyIconPath,
                        Glyph = ResourceHelper.CopyGlyph,
                        Score = ScoreInterval12,
                        AddSelectedCount = false,
                        Action = (c) =>
                        {
                            CopyFilePathToClipboard(clipboardDataPair, filePaths);
                            return true;
                        }
                    });

                    // Copy file name
                    results.Add(new Result
                    {
                        Title = Localize.flowlauncher_plugin_clipboardplus_copy_file_name_title(),
                        SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_file_name_subtitle(),
                        IcoPath = PathHelper.CopyIconPath,
                        Glyph = ResourceHelper.CopyGlyph,
                        Score = ScoreInterval11,
                        AddSelectedCount = false,
                        Action = (c) =>
                        {
                            CopyFileNamesToClipboard(clipboardDataPair, filePaths);
                            return true;
                        }
                    });

                    // Copy file content
                    if (filePaths.Length == 1 && existingFilePaths.Length == 1)
                    {
                        results.Add(new Result
                        {
                            Title = Localize.flowlauncher_plugin_clipboardplus_copy_file_content_title(),
                            SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_file_content_subtitle(),
                            IcoPath = PathHelper.CopyIconPath,
                            Glyph = ResourceHelper.CopyGlyph,
                            Score = ScoreInterval10,
                            AddSelectedCount = false,
                            Action = (c) =>
                            {
                                CopyFileContentToClipboard(clipboardDataPair, existingFilePaths);
                                return true;
                            }
                        });
                    }

                    // Copy file by sorting paths
                    if (filePaths.Length > 1)
                    {
                        results.AddRange(
                        [
                            new Result
                            {
                                Title = Localize.flowlauncher_plugin_clipboardplus_copy_sort_name_asc_title(),
                                SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_sort_name_asc_subtitle(),
                                IcoPath = PathHelper.CopyIconPath,
                                Glyph = ResourceHelper.CopyGlyph,
                                Score = ScoreInterval9,
                                AddSelectedCount = false,
                                Action = (c) =>
                                {
                                    CopyBySortingNameToClipboard(clipboardDataPair, true);
                                    return true;
                                }
                            },
                            new Result
                            {
                                Title = Localize.flowlauncher_plugin_clipboardplus_copy_sort_name_desc_title(),
                                SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_sort_name_desc_subtitle(),
                                IcoPath = PathHelper.CopyIconPath,
                                Glyph = ResourceHelper.CopyGlyph,
                                Score = ScoreInterval8,
                                AddSelectedCount = false,
                                Action = (c) =>
                                {
                                    CopyBySortingNameToClipboard(clipboardDataPair, false);
                                    return true;
                                }
                            },
                            new Result
                            {
                                Title = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_asc_as_path_title(),
                                SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_asc_as_path_subtitle(),
                                IcoPath = PathHelper.CopyIconPath,
                                Glyph = ResourceHelper.CopyGlyph,
                                Score = ScoreInterval7,
                                AddSelectedCount = false,
                                Action = (c) =>
                                {
                                    CopyFilePathsBySortingPathToClipboard(clipboardDataPair, true);
                                    return true;
                                }
                            },
                            new Result
                            {
                                Title = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_desc_as_path_title(),
                                SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_desc_as_path_subtitle(),
                                IcoPath = PathHelper.CopyIconPath,
                                Glyph = ResourceHelper.CopyGlyph,
                                Score = ScoreInterval6,
                                AddSelectedCount = false,
                                Action = (c) =>
                                {
                                    CopyFilePathsBySortingPathToClipboard(clipboardDataPair, false);
                                    return true;
                                }
                            },
                            new Result
                            {
                                Title = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_asc_as_text_title(),
                                SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_asc_as_text_subtitle(),
                                IcoPath = PathHelper.CopyIconPath,
                                Glyph = ResourceHelper.CopyGlyph,
                                Score = ScoreInterval5,
                                AddSelectedCount = false,
                                Action = (c) =>
                                {
                                    CopyFileNamesBySortingPathToClipboard(clipboardDataPair, true);
                                    return true;
                                }
                            },
                            new Result
                            {
                                Title = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_desc_as_text_title(),
                                SubTitle = Localize.flowlauncher_plugin_clipboardplus_copy_sort_path_desc_as_text_subtitle(),
                                IcoPath = PathHelper.CopyIconPath,
                                Glyph = ResourceHelper.CopyGlyph,
                                Score = ScoreInterval4,
                                AddSelectedCount = false,
                                Action = (c) =>
                                {
                                    CopyFileNamesBySortingPathToClipboard(clipboardDataPair, false);
                                    return true;
                                }
                            },
                        ]);
                    }
                }
                break;
            default:
                break;
        }

        if (!UseWindowsClipboardHistoryOnly)
        {
            // Save
            if (!saved)
            {
                results.Add(new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_save_title(),
                    SubTitle = Localize.flowlauncher_plugin_clipboardplus_save_subtitle(),
                    IcoPath = PathHelper.DatabaseIconPath,
                    Glyph = ResourceHelper.DatabaseGlyph,
                    Score = ScoreInterval3,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        _ = SaveToDatabaseAsync(clipboardDataPair, true);
                        return false;
                    }
                });
            }

            // Pin
            string pinResultTitle, pinResultSubtitle;
            if (pinned)
            {
                pinResultTitle = Localize.flowlauncher_plugin_clipboardplus_unpin_title();
                pinResultSubtitle = Localize.flowlauncher_plugin_clipboardplus_unpin_subtitle();
            }
            else
            {
                pinResultTitle = Localize.flowlauncher_plugin_clipboardplus_pin_title();
                pinResultSubtitle = Localize.flowlauncher_plugin_clipboardplus_pin_subtitle();
            }
            results.Add(new Result
            {
                Title = pinResultTitle,
                SubTitle = pinResultSubtitle,
                IcoPath = PathHelper.GetPinIconPath(pinned),
                Glyph = ResourceHelper.GetPinGlyph(pinned),
                Score = ScoreInterval2,
                AddSelectedCount = false,
                Action = (c) =>
                {
                    _ = PinRecordAsync(clipboardDataPair, true);
                    return false;
                }
            });
        }

        if (UseWindowsClipboardHistoryOnly)
        {
            results.Add(new Result
            {
                Title = Localize.flowlauncher_plugin_clipboardplus_delete_title(),
                SubTitle = Localize.flowlauncher_plugin_clipboardplus_delete_system_list_subtitle(),
                IcoPath = PathHelper.DeleteIconPath,
                Glyph = ResourceHelper.DeleteGlyph,
                Score = ScoreInterval1,
                AddSelectedCount = false,
                Action = (c) =>
                {
                    _ = RemoveFromListDatabaseAsync(clipboardDataPair, true);
                    WindowsClipboardHelper.DeleteItemFromHistory(clipboardData);
                    return false;
                }
            });
        }
        else
        {
            // Delete
            if (!fromSystem)
            {
                var deleteResultSubtitle = saved ?
                    Localize.flowlauncher_plugin_clipboardplus_delete_both_subtitle() :
                    Localize.flowlauncher_plugin_clipboardplus_delete_list_subtitle();
                results.Add(new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_delete_title(),
                    SubTitle = deleteResultSubtitle,
                    IcoPath = PathHelper.DeleteIconPath,
                    Glyph = ResourceHelper.DeleteGlyph,
                    Score = ScoreInterval1,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        _ = RemoveFromListDatabaseAsync(clipboardDataPair, true);
                        return false;
                    }
                });
            }
            else
            {
                var deleteResultSubtitle = saved ?
                    Localize.flowlauncher_plugin_clipboardplus_delete_system_both_subtitle() :
                    Localize.flowlauncher_plugin_clipboardplus_delete_system_list_subtitle();
                results.Add(new Result
                {
                    Title = Localize.flowlauncher_plugin_clipboardplus_delete_title(),
                    SubTitle = deleteResultSubtitle,
                    IcoPath = PathHelper.DeleteIconPath,
                    Glyph = ResourceHelper.DeleteGlyph,
                    Score = ScoreInterval1,
                    AddSelectedCount = false,
                    Action = (c) =>
                    {
                        _ = RemoveFromListDatabaseAsync(clipboardDataPair, true);
                        WindowsClipboardHelper.DeleteItemFromHistory(clipboardData);
                        return false;
                    }
                });
            }
        }

        return results;
    }

    #endregion

    #region IPluginI18n Interface

    public string GetTranslatedPluginTitle()
    {
        return Localize.flowlauncher_plugin_clipboardplus_plugin_name();
    }

    public string GetTranslatedPluginDescription()
    {
        return Localize.flowlauncher_plugin_clipboardplus_plugin_description();
    }

    public void OnCultureInfoChanged(CultureInfo cultureInfo)
    {
        Context.LogDebug(ClassName, $"Culture info changed to {cultureInfo}");
        CultureInfo = cultureInfo;
        CultureInfoChanged?.Invoke(this, cultureInfo);
    }

    #endregion

    #region IResultUpdated Interface

    public event ResultUpdatedEventHandler? ResultsUpdated;

    #endregion

    #region ISavable Interface

    // Warning: This method will be called after dispose.
    public void Save()
    {
        // We don't need to save plugin settings because it will be called by FL.
        // Context?.API.SaveSettingJsonStorage<Settings>();
    }

    #endregion

    #region ISettingProvider Interface

    public Control CreateSettingPanel()
    {
        Context.LogDebug(ClassName, $"Settings Panel: {Settings}");
        return new SettingsPanel(this);
    }

    #endregion

    #region Clipboard Monitor

    private async void ClipboardMonitor_OnClipboardChanged(object? sender, ClipboardChangedEventArgs e)
    {
        Context.LogDebug(ClassName, "Clipboard changed");

        if (sender is not IClipboardMonitor clipboardMonitor)
        {
            return;
        }

        await RecordsLock.WaitAsync();
        try
        {
            // get clipboard data
            var clipboardData = GetClipboardDataItem(e.Content, e.DataType, StringUtils.GetGuid(), DateTime.Now, e.SourceApplication, clipboardMonitor.ClipboardText, clipboardMonitor.ClipboardRtfText, true, out var encodedImage);

            // add clipboard data
            if (!clipboardData.IsNull())
            {
                AddClipboardDataItem(clipboardData, encodedImage);
            }
        }
        finally
        {
            RecordsLock.Release();
        }
    }

    public ClipboardData GetClipboardDataItem(object? content, DataType dataType, string hashId, DateTime createTime, SourceApplication source, string clipboardText, string clipboardRtfText) =>
        GetClipboardDataItem(content, dataType, hashId, createTime, source, clipboardText, clipboardRtfText, false, out _);

    private ClipboardData GetClipboardDataItem(object? content, DataType dataType, string hashId, DateTime createTime, SourceApplication source, string clipboardText, string clipboardRtfText, bool filterConsecutive, out string? encodedImage)
    {
        encodedImage = null;
        if (content is null || dataType == DataType.Other)
        {
            return ClipboardData.NULL;
        }

        // init clipboard data
        var saved =
            UseWindowsClipboardHistoryOnly != true &&
            (Settings.KeepText && dataType == DataType.PlainText ||
            Settings.KeepText && dataType == DataType.RichText ||
            Settings.KeepImages && dataType == DataType.Image ||
            Settings.KeepFiles && dataType == DataType.Files);
        var clipboardData = new ClipboardData(content, dataType, Settings.EncryptData, out encodedImage)
        {
            HashId = hashId,
            SenderApp = source.Name,
            InitScore = ScoreHelper.CurrentScore,
            CreateTime = createTime,
            EncryptKeyMd5 = StringUtils.EncryptKeyMd5,
            CachedImagePath = string.Empty,
            Pinned = false,
            Saved = saved
        };

        if (!saved) encodedImage = null;

        // filter duplicate data
        if (filterConsecutive && RecordsList.Count != 0 && RecordsList.First().ClipboardData.DataEquals(clipboardData))
        {
            encodedImage = null;
            return ClipboardData.NULL;
        }

        // process clipboard data
        if (dataType == DataType.Image && Settings.CacheImages)
        {
            var imageName = StringUtils.FormatImageName(Settings.CacheFormat, clipboardData.CreateTime,
                string.IsNullOrEmpty(clipboardData.SenderApp) ?
                Localize.flowlauncher_plugin_clipboardplus_unknown() :
                clipboardData.SenderApp);
            var imagePath = FileUtils.SaveNewImageCache(clipboardData, PathHelper.ImageCachePath, imageName, out var ownership);
            clipboardData.CachedImagePath = imagePath;
            if (ownership is not null) RememberOwnedCacheFile(ownership with { WindowsAssociated = !filterConsecutive });
        }
        if (dataType == DataType.RichText)
        {
            // due to some bugs, we need to convert rtf to plain text
            if (string.IsNullOrEmpty(clipboardText))
            {
                clipboardData.PlainText = StringUtils.ConvertRtfToPlainText(clipboardRtfText);
            }
            else
            {
                clipboardData.PlainText = clipboardText;
            }
        }

        return clipboardData;
    }

    private long captureGeneration;

    private void AddClipboardDataItem(ClipboardData clipboardData) => AddClipboardDataItem(clipboardData, null);

    private void AddClipboardDataItem(ClipboardData clipboardData, string? encodedImage)
    {
        // Called under RecordsLock. An in-flight cleanup must not trim a newer capture.
        captureGeneration++;
        // add to list and database if no repeat
        RecordsList.AddFirst(new ClipboardDataPair()
        {
            ClipboardData = clipboardData,
            PreviewPanel = new Lazy<UserControl>(() => new PreviewPanel(this, clipboardData))
        });

        // update score
        ScoreHelper.Add();

        // save to database if needed
        if (clipboardData.Saved)
        {
            _ = encodedImage is null
                ? Database.AddOneRecordAsync(clipboardData, true)
                : Database.AddOneRecordWithEncodedImageAsync(clipboardData, encodedImage);
            Context.LogDebug(ClassName, "Record added to list and database");
        }
        else
        {
            Context.LogDebug(ClassName, "Record added to list");
        }

        // remove last record if needed
        if (!Settings.AutomaticCleanup) TrimUnpinnedCapacity(Settings.MaxRecords);

        if (Settings.AutomaticCleanup) EnforceCaptureOverflowLimit();
        ScheduleAutomaticCleanup();
        // collect garbage
        GarbageCollect();
    }

    // Called under RecordsLock. A safety pin must not be discarded merely
    // because automatic duplicate cleanup is disabled or the list is full.
    private bool RemoveOldestUnpinnedRecord()
    {
        var node = RecordsList.Last;
        while (node is not null && node.Value.ClipboardData.Pinned) node = node.Previous;
        if (node is null) return false;
        RecordsList.Remove(node);
        node.Value.Dispose();
        return true;
    }

    // Called under RecordsLock; capacity unloads records, never deletes database rows.
    private void TrimUnpinnedCapacity(int limit)
    {
        var excess = RecordsList.Count(pair => !pair.ClipboardData.Pinned) - Math.Max(0, limit);
        while (excess-- > 0 && RemoveOldestUnpinnedRecord()) { }
    }

    public async Task EnforceHistoryCapacityAsync()
    {
        await RecordsLock.WaitAsync();
        try
        {
            if (Settings.AutomaticCleanup) EnforceCaptureOverflowLimit();
            else TrimUnpinnedCapacity(Settings.MaxRecords);
            ScoreHelper.Reset(RecordsList);
        }
        finally { RecordsLock.Release(); }
        await PublishCurrentResultsAsync();
        ScheduleAutomaticCleanup();
    }

    private void AddClipboardDataItem(List<ClipboardData> clipboardDataList)
    {
        if (clipboardDataList.Count > 0) captureGeneration++;
        var trimmedOverflow = false;

        foreach (var clipboardData in clipboardDataList)
        {
            // add to list and database if no repeat
            RecordsList.AddFirst(new ClipboardDataPair()
            {
                ClipboardData = clipboardData,
                PreviewPanel = new Lazy<UserControl>(() => new PreviewPanel(this, clipboardData))
            });

            // save to database if needed
            if (clipboardData.Saved)
            {
                _ = Database.AddOneRecordAsync(clipboardData, true);  // no need to wait
                Context.LogDebug(ClassName, "Record added to list and database");
            }
            else
            {
                Context.LogDebug(ClassName, "Record added to list");
            }
            if (Settings.AutomaticCleanup) trimmedOverflow |= EnforceCaptureOverflowLimit(false);
        }

        if (trimmedOverflow) NotifyCaptureOverflowTrimmed();
        if (!Settings.AutomaticCleanup) TrimUnpinnedCapacity(Settings.MaxRecords);

        ScheduleAutomaticCleanup();
        // collect garbage
        GarbageCollect();
    }

    #endregion

    #region Windows Clipboard Helper

    public bool CheckUseWindowsClipboardHistoryOnly()
    {
        if (!WindowsClipboardHelper.IsClipboardHistorySupported())
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_windows_history_not_supported());
            return false;
        }

        return true;
    }

    public async void EnableWindowsClipboardHelper(bool load)
    {
        if (UseWindowsClipboardHistoryOnly)
        {
            WindowsClipboardHelper.OnHistoryItemAdded += WindowsClipboardHelper_OnHistoryItemAdded;
        }
        WindowsClipboardHelper.OnHistoryItemRemoved += WindowsClipboardHelper_OnHistoryItemRemoved;
        WindowsClipboardHelper.OnHistoryReadCompleted += WindowsClipboardHelper_OnHistoryReadCompleted;
        WindowsClipboardHelper.OnHistoryItemPinUpdated += WindowsClipboardHelper_OnHistoryItemPinUpdated;
        WindowsClipboardHelper.OnHistoryEnabledChanged += WindowsClipboardHelper_OnHistoryEnabledChanged;
        WindowsClipboardHelper.EnableClipboardHistory();
        if (load)
        {
            await RecordsLock.WaitAsync();
            try
            {
                await InitRecordsFromSystemAsync(true);
            }
            finally
            {
                RecordsLock.Release();
            }
        }
    }

    public async void DisableWindowsClipboardHelper(bool remove)
    {
        WindowsClipboardHelper.DisableClipboardHistory();
        if (UseWindowsClipboardHistoryOnly)
        {
            WindowsClipboardHelper.OnHistoryItemAdded -= WindowsClipboardHelper_OnHistoryItemAdded;
        }
        WindowsClipboardHelper.OnHistoryItemRemoved -= WindowsClipboardHelper_OnHistoryItemRemoved;
        WindowsClipboardHelper.OnHistoryReadCompleted -= WindowsClipboardHelper_OnHistoryReadCompleted;
        WindowsClipboardHelper.OnHistoryItemPinUpdated -= WindowsClipboardHelper_OnHistoryItemPinUpdated;
        WindowsClipboardHelper.OnHistoryEnabledChanged -= WindowsClipboardHelper_OnHistoryEnabledChanged;
        if (remove)
        {
            await RecordsLock.WaitAsync();
            try
            {
                RemoveRecordsFromSystem();
            }
            finally
            {
                RecordsLock.Release();
            }
        }
    }

    private async void WindowsClipboardHelper_OnHistoryItemAdded(object? sender, ClipboardData e)
    {
        await RecordsLock.WaitAsync();
        try { AddClipboardDataItem(e); }
        finally { RecordsLock.Release(); }
        RequestWindowsEvidenceRefresh();
    }

    private async void WindowsClipboardHelper_OnHistoryItemRemoved(object? sender, string[] e)
    {
        List<string> removedCachePaths = [];
        await RecordsLock.WaitAsync();
        try
        {
            if (Settings.AutomaticCleanup)
                removedCachePaths = RecordsList.Where(pair => pair.ClipboardData.FromWindowsClipboardHistory() &&
                    e.Contains(pair.ClipboardData.HashId) && pair.ClipboardData.DataType == DataType.Image)
                    .Select(pair => pair.ClipboardData.CachedImagePath).Where(path => !string.IsNullOrEmpty(path)).ToList();
            RemoveRecordsFromSystem(r => e.Contains(r.HashId));
        }
        finally
        {
            RecordsLock.Release();
        }
        RequestWindowsEvidenceRefresh();
        if (removedCachePaths.Count > 0)
        {
            foreach (var path in removedCachePaths) removedWindowsCachePaths.Enqueue(path);
            ScheduleAutomaticCleanup();
        }
    }

    private async void WindowsClipboardHelper_OnHistoryItemPinUpdated(object? sender, (string HashId, bool Pinned) e)
    {
        await RecordsLock.WaitAsync();
        try
        {
            var clipboardDataPair = RecordsList.FirstOrDefault(r => r.ClipboardData.HashId == e.HashId);
            if (clipboardDataPair != null)
            {
                if (clipboardDataPair.ClipboardData.Pinned != e.Pinned)
                {
                    clipboardDataPair.TogglePinned();
                    if (!UseWindowsClipboardHistoryOnly)
                    {
                        if (clipboardDataPair.ClipboardData.Saved)
                            _ = Database.PinOneRecordAsync(clipboardDataPair.ClipboardData);
                        else if (e.Pinned)
                        {
                            var pinnedData = clipboardDataPair.ClipboardData;
                            pinnedData.Saved = true;
                            clipboardDataPair.ClipboardData = pinnedData;
                            _ = Database.AddOneRecordAsync(pinnedData, true);
                        }
                    }
                }
                RecordsList.Remove(clipboardDataPair);
                RecordsList.AddFirst(clipboardDataPair);
                if (!e.Pinned)
                {
                    if (Settings.AutomaticCleanup) EnforceCaptureOverflowLimit();
                    else TrimUnpinnedCapacity(Settings.MaxRecords);
                }
                Context.LogDebug(ClassName, "Updated record pin status");
            }
            else
            {
                Context.LogDebug(ClassName, "Unable to find record for pin update");
            }
        }
        finally
        {
            RecordsLock.Release();
        }
        RequestWindowsEvidenceRefresh();
        ScheduleAutomaticCleanup();
        Context?.ReQuery(false);
    }

    private async void WindowsClipboardHelper_OnHistoryEnabledChanged(object? sender, bool e)
    {
        await RecordsLock.WaitAsync();
        try
        {
            if (e)
            {
                await InitRecordsFromSystemAsync(true);
            }
            else
            {
                RemoveRecordsFromSystem();
            }
        }
        finally
        {
            RecordsLock.Release();
        }
    }

    #endregion

    #region List & Database & Windows History

    public async Task RunRetentionExpiryAsync()
    {
        if (UseWindowsClipboardHistoryOnly) return;
        await RecordsLock.WaitAsync();
        try
        {
            var expired = await Database.ExpireRecordsAsync(Settings.KeepTimePairs.Select(pair =>
                ((int)pair.Item1, pair.Item2.ToKeepTime())), Settings.ProtectPinsFromExpiry);
            var ids = new HashSet<string>(expired, StringComparer.OrdinalIgnoreCase);
            var node = RecordsList.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.ClipboardData.Saved && ids.Contains(node.Value.ClipboardData.HashId))
                {
                    var pair = node.Value;
                    RecordsList.Remove(node);
                    pair.Dispose();
                }
                node = next;
            }
            ScoreHelper.Reset(RecordsList);
        }
        finally { RecordsLock.Release(); }
        await PublishCurrentResultsAsync();
    }

    public async Task InitRecordsFromDatabaseAndSystemAsync(bool database, bool system)
    {
        // manage database
        IEnumerable<ClipboardDataPair>? databaseDataPairs = null;
        if (database)
        {
            if (!UseWindowsClipboardHistoryOnly)
            {
                // Run one atomic pass before restoring database history.
                try
                {
                    await Database.ExpireRecordsAsync(Settings.KeepTimePairs.Select(pair =>
                        ((int)pair.Item1, pair.Item2.ToKeepTime())), Settings.ProtectPinsFromExpiry);
                }
                catch (Exception e)
                {
                    Context.LogException(ClassName, "Clearing expired records failed", e);
                }

                // restore database records
                var records = await Database.GetAllRecordsAsync(true);
                if (records.Count != 0)
                {
                    databaseDataPairs = records.Select(record => new ClipboardDataPair()
                    {
                        ClipboardData = record,
                        PreviewPanel = new Lazy<UserControl>(() => new PreviewPanel(this, record))
                    });
                }
            }
        }

        await RecordsLock.WaitAsync();
        try
        {
            // clean records
            ClearRecordsList();

            // restore database records
            if (databaseDataPairs != null)
            {
                RecordsList = new LinkedList<ClipboardDataPair>(databaseDataPairs);
            }

            // restore Windows clipboard history items
            if (system)
            {
                if (UseWindowsClipboardHistoryOnly)
                {
                    await InitRecordsFromSystemAsync(false);
                }
                else if (Settings.SyncWindowsClipboardHistory)
                {
                    await InitRecordsFromSystemAsync(true);
                }
            }
            if (Settings.AutomaticCleanup) EnforceCaptureOverflowLimit();
            else TrimUnpinnedCapacity(Settings.MaxRecords);
        }
        finally
        {
            RecordsLock.Release();
        }

        ScheduleAutomaticCleanup();
        RequestWindowsEvidenceRefresh();
        // collect garbage
        GarbageCollect();

        Context.LogDebug(ClassName, $"Restored {RecordsList.Count} records successfully");
    }

    private async Task InitRecordsFromSystemAsync(bool check)
    {
        // get history items
        (List<ClipboardData>? Items, bool PinStateTrusted) history;
        if (check)
        {
            var latestDateTime = RecordsList.Count != 0 ? RecordsList.Max(p => p.ClipboardData.CreateTime) : DateTime.MinValue;
            history = await WindowsClipboardHelper.GetLaterHistoryItemsWithPinStateAsync(latestDateTime);
        }
        else
        {
            history = await WindowsClipboardHelper.GetHistoryItemsWithPinStateAsync();
        }

        // add history items
        if (history.Items is { Count: > 0 } historyItems)
        {
            var protectedImport = WindowsImportPinPolicy.ProtectUnverified(historyItems, history.PinStateTrusted, !UseWindowsClipboardHistoryOnly);
            AddClipboardDataItem(historyItems);
            if (protectedImport)
                Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_windows_pin_warning_title(),
                    Localize.flowlauncher_plugin_clipboardplus_windows_pin_unknown());
        }
        RequestWindowsEvidenceRefresh();
    }

    private void RemoveRecordsFromSystem(Func<ClipboardData, bool>? func = null)
    {
        List<ClipboardDataPair> recordsToRemove;
        if (func == null)
        {
            recordsToRemove = [.. RecordsList.Where(r => r.ClipboardData.FromWindowsClipboardHistory())];
        }
        else
        {
            recordsToRemove = [.. RecordsList.Where(r => r.ClipboardData.FromWindowsClipboardHistory() && func(r.ClipboardData))];
        }
        while (recordsToRemove.Count != 0)
        {
            var record = recordsToRemove.First();
            RecordsList.Remove(record);
            recordsToRemove.Remove(record);
            record.Dispose();
        }
        ScoreHelper.Reset(RecordsList);
        GarbageCollect();
    }

    private async Task<int> DeleteAllRecordsFromListAsync()
    {
        await RecordsLock.WaitAsync();
        try
        {
            var number = RecordsList.Count;
            ClearRecordsList();
            ScoreHelper.Reset();
            Context.LogDebug(ClassName, "Deleted all records from list");
            return number;
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
    }

    private async Task<int> DeleteAllRecordsFromListDatabaseAsync()
    {
        await RecordsLock.WaitAsync();
        try
        {
            var number = RecordsList.Count;
            ClearRecordsList();
            _ = Database.DeleteAllRecordsAsync();
            ScoreHelper.Reset();
            Context.LogDebug(ClassName, "Deleted all records from list and database");
            return number;
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
    }

    private async Task<int> DeleteUnpinnedRecordsFromListDatabaseAsync()
    {
        await RecordsLock.WaitAsync();
        try
        {
            var unpinnedRecords = RecordsList.Where(r => !r.ClipboardData.Pinned).ToArray();
            var number = unpinnedRecords.Length;
            foreach (var record in unpinnedRecords)
            {
                record.Dispose();
                RecordsList.Remove(record);
            }
            _ = Database.DeleteUnpinnedRecordsAsync();
            ScoreHelper.Reset(RecordsList);
            Context.LogDebug(ClassName, "Deleted unpinned records from list and database");
            return number;
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
    }

    private async Task<int> DeleteInvalidRecordsFromListDatabaseAsync()
    {
        await RecordsLock.WaitAsync();
        try
        {
            var invalidRecords = RecordsList.Where(r => r.ClipboardData.DataToValid() is null).ToArray();
            var number = invalidRecords.Length;
            foreach (var record in invalidRecords)
            {
                record.Dispose();
                RecordsList.Remove(record);
                _ = Database.DeleteOneRecordAsync(record.ClipboardData);
            }
            ScoreHelper.Reset(RecordsList);
            Context.LogDebug(ClassName, "Deleted invalid records from list and database");
            return number;
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
    }

    private async Task SaveToDatabaseAsync(ClipboardDataPair clipboardDataPair, bool requery)
    {
        await RecordsLock.WaitAsync();
        try
        {
            var clipboardData = clipboardDataPair.ClipboardData;
            if (!clipboardData.Saved)
            {
                clipboardData.Saved = true;
                RecordsList.Remove(clipboardDataPair);
                RecordsList.AddFirst(clipboardDataPair);
                if (requery)
                {
                    ReQuery();
                }
                _ = Database.AddOneRecordAsync(clipboardData, true);
                Context.LogDebug(ClassName, "Saved record to database");
            }
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
    }

    private async Task RemoveFromListAsync(ClipboardDataPair clipboardDataPair, bool requery)
    {
        await RecordsLock.WaitAsync();
        try
        {
            if (RecordsList.First?.Value == clipboardDataPair)
            {
                ScoreHelper.Subtract();
            }
            clipboardDataPair.Dispose();
            RecordsList.Remove(clipboardDataPair);
            if (requery)
            {
                ReQuery();
            }
            Context.LogDebug(ClassName, "Removed record from list");
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
    }

    private async Task RemoveFromListDatabaseAsync(ClipboardDataPair clipboardDataPair, bool requery)
    {
        await RecordsLock.WaitAsync();
        try
        {
            if (RecordsList.First?.Value == clipboardDataPair)
            {
                ScoreHelper.Subtract();
            }
            var clipboardData = clipboardDataPair.ClipboardData;
            clipboardDataPair.Dispose();
            RecordsList.Remove(clipboardDataPair);
            if (requery)
            {
                ReQuery();
            }
            _ = Database.DeleteOneRecordAsync(clipboardData);
            Context.LogDebug(ClassName, "Removed record from list and database");
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
    }

    private async Task PinRecordAsync(ClipboardDataPair clipboardDataPair, bool requery)
    {
        await RecordsLock.WaitAsync();
        try
        {
            clipboardDataPair.TogglePinned();
            RecordsList.Remove(clipboardDataPair);
            RecordsList.AddFirst(clipboardDataPair);
            if (requery)
            {
                ReQuery();
            }
            if (!clipboardDataPair.ClipboardData.Saved)
            {
                _ = Database.AddOneRecordAsync(clipboardDataPair.ClipboardData, true);
            }
            else
            {
                _ = Database.PinOneRecordAsync(clipboardDataPair.ClipboardData);
            }
            if (!clipboardDataPair.ClipboardData.Pinned)
            {
                if (Settings.AutomaticCleanup) EnforceCaptureOverflowLimit();
                else TrimUnpinnedCapacity(Settings.MaxRecords);
            }
            Context.LogDebug(ClassName, "Toggled record pin");
        }
        finally
        {
            RecordsLock.Release();
            GarbageCollect();
        }
        ScheduleAutomaticCleanup();
    }

    private void ClearRecordsList()
    {
        foreach (var record in RecordsList)
        {
            record.Dispose();
        }
        RecordsList.Clear();
    }

    private void ReQuery()
    {
        Context.ReQuery(false);
    }

    #endregion

    #region Query Result

    private Result? GetResultFromClipboardData(ClipboardDataPair clipboardDataPair, string? querySearch)
    {
        try
        {
            var clipboardData = clipboardDataPair.ClipboardData;
            if (IsWindowsSuppressed(clipboardDataPair)) return null;

            // Get score
            int score;
            if (string.IsNullOrEmpty(querySearch))
            {
                score = clipboardData.GetScore(Settings.RecordOrder);
            }
            else
            {
                var clipboardText = clipboardData.GetText(CultureInfo);
                if (string.IsNullOrEmpty(clipboardText))
                {
                    return null;
                }

                // Check the fuzzy match
                var match = Context.FuzzySearch(querySearch, clipboardData.GetText(CultureInfo));
                if (!match.IsSearchPrecisionScoreMet())
                {
                    return null;
                }
                score = match.Score;
            }

            // Return result
            var title = clipboardData.GetTitle(CultureInfo);
            var limitedTitle = title.Length > MaxTitleToolTipCharacters ? title.AsSpan(0, MaxTitleToolTipCharacters).ToString() : title;
            var limitedDisplayTitle = title.Length > MaxTitleToolTipCharacters ? string.Concat(title.AsSpan(0, MaxTitleToolTipCharacters), "...") : title;
            var titleText = clipboardData.GetText(CultureInfo);
            var limitedDisplayTitleText = titleText.Length > MaxTitleToolTipCharacters ? string.Concat(titleText.AsSpan(0, MaxTitleToolTipCharacters), "...") : titleText;
            var subtitle = Settings.UseRelativeTime
                ? clipboardData.GetSubtitleWithTimestamp(RelativeTimeFormatter.Format(
                    clipboardData.CreateTime, DateTime.Now, CultureInfo,
                    Localize.flowlauncher_plugin_clipboardplus_relative_just_now(),
                    minutes => minutes == 1
                        ? Localize.flowlauncher_plugin_clipboardplus_relative_minute(minutes)
                        : Localize.flowlauncher_plugin_clipboardplus_relative_minutes(minutes),
                    time => Localize.flowlauncher_plugin_clipboardplus_relative_today(time),
                    time => Localize.flowlauncher_plugin_clipboardplus_relative_yesterday(time),
                    (day, time) => Localize.flowlauncher_plugin_clipboardplus_relative_weekday(day, time)))
                : clipboardData.GetSubtitle(CultureInfo);
            return new Result
            {
                Title = limitedDisplayTitle,
                AutoCompleteText = limitedTitle,
                QuerySuggestionText = limitedTitle,
                TitleToolTip = limitedDisplayTitleText,
                SubTitle = subtitle,
                SubTitleToolTip = subtitle,
                Icon = () => clipboardData.Icon,
                Glyph = clipboardData.Glyph,
                CopyText = titleText,
                Score = clipboardData.GetScore(Settings.RecordOrder),
                AddSelectedCount = false,
                ContextData = clipboardDataPair,
                PreviewPanel = clipboardDataPair.PreviewPanel,
                Action = (c) =>
                {
                    switch (Settings.ClickAction)
                    {
                        case ClickAction.Copy:
                            CopyToClipboard(clipboardDataPair);
                            break;
                        case ClickAction.CopyPaste:
                            Context!.API.HideMainWindow();
                            CopyToClipboard(clipboardDataPair);
                            _ = WaitWindowHideAndSimulatePaste();
                            break;
                        case ClickAction.CopyDeleteList:
                            CopyToClipboard(clipboardDataPair);
                            _ = RemoveFromListAsync(clipboardDataPair, false);
                            break;
                        case ClickAction.CopyDeleteListDatabase:
                            CopyToClipboard(clipboardDataPair);
                            _ = RemoveFromListDatabaseAsync(clipboardDataPair, false);
                            break;
                        case ClickAction.CopyPasteDeleteList:
                            Context!.API.HideMainWindow();
                            CopyToClipboard(clipboardDataPair);
                            _ = RemoveFromListAsync(clipboardDataPair, false);
                            _ = WaitWindowHideAndSimulatePaste();
                            break;
                        case ClickAction.CopyPasteDeleteListDatabase:
                            Context!.API.HideMainWindow();
                            CopyToClipboard(clipboardDataPair);
                            _ = RemoveFromListDatabaseAsync(clipboardDataPair, false);
                            _ = WaitWindowHideAndSimulatePaste();
                            break;
                        default:
                            break;
                    }
                    return true;
                },
            };
        }
        catch (Exception e)
        {
            Context.LogException(ClassName, "Get result from clipboard data failed", e);
            return null;
        }
    }

    private async Task WaitWindowHideAndSimulatePaste()
    {
        while (Context!.API.IsMainWindowVisible())
        {
            await Task.Delay(RetryInterval);
        }
        new InputSimulator().Keyboard.ModifiedKeyStroke(
            VirtualKeyCode.CONTROL,
            VirtualKeyCode.VK_V
        );
    }

    #endregion

    #region Clipboard Actions

    #region Default Copy

    private void CopyToClipboard(ClipboardDataPair clipboardDataPair)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        var dataType = clipboardData.DataType;
        switch (dataType)
        {
            case DataType.PlainText:
                CopyOriginallyToClipboard(clipboardDataPair);
                break;
            case DataType.RichText:
                switch (Settings.DefaultRichTextCopyOption)
                {
                    case DefaultRichTextCopyOption.Rtf:
                        CopyOriginallyToClipboard(clipboardDataPair);
                        break;
                    case DefaultRichTextCopyOption.Plain:
                        CopyAsPlainTextToClipboard(clipboardDataPair);
                        break;
                    default:
                        break;
                }
                break;
            case DataType.Image:
                switch (Settings.DefaultImageCopyOption)
                {
                    case DefaultImageCopyOption.Image:
                        CopyOriginallyToClipboard(clipboardDataPair);
                        break;
                    case DefaultImageCopyOption.File:
                        CopyImageFileToClipboard(clipboardDataPair);
                        break;
                    default:
                        break;
                }
                break;
            case DataType.Files:
                switch (Settings.DefaultFilesCopyOption)
                {
                    case DefaultFilesCopyOption.Files:
                        CopyOriginallyToClipboard(clipboardDataPair);
                        break;
                    case DefaultFilesCopyOption.Path:
                        if (clipboardData.Data is string[] filePaths)
                        {
                            CopyFilePathToClipboard(clipboardDataPair, filePaths);
                        }
                        else
                        {
                            CopyOriginallyToClipboard(clipboardDataPair);
                        }
                        break;
                    case DefaultFilesCopyOption.Content:
                        if (clipboardData.Data is string[] filePaths1)
                        {
                            var existingFilePaths = filePaths1.Where(FileUtils.Exists).ToArray();
                            if (filePaths1.Length == 1 && existingFilePaths.Length == 1)
                            {
                                CopyFileContentToClipboard(clipboardDataPair, filePaths1);
                            }
                            else
                            {
                                CopyOriginallyToClipboard(clipboardDataPair);
                            }
                        }
                        else
                        {
                            CopyOriginallyToClipboard(clipboardDataPair);
                        }
                        break;
                    case DefaultFilesCopyOption.FileName:
                        if (clipboardData.Data is string[] filePaths2)
                        {
                            CopyFileNamesToClipboard(clipboardDataPair, filePaths2);
                        }
                        else
                        {
                            CopyOriginallyToClipboard(clipboardDataPair);
                        }
                        break;
                    case DefaultFilesCopyOption.NameAsc:
                        CopyBySortingNameToClipboard(clipboardDataPair, true);
                        break;
                    case DefaultFilesCopyOption.NameDesc:
                        CopyBySortingNameToClipboard(clipboardDataPair, false);
                        break;
                    case DefaultFilesCopyOption.PathTextAsc:
                        CopyFilePathsBySortingPathToClipboard(clipboardDataPair, true);
                        break;
                    case DefaultFilesCopyOption.PathTextDesc:
                        CopyFilePathsBySortingPathToClipboard(clipboardDataPair, false);
                        break;
                    case DefaultFilesCopyOption.FileNameTextAsc:
                        CopyFileNamesBySortingPathToClipboard(clipboardDataPair, true);
                        break;
                    case DefaultFilesCopyOption.FileNameTextDesc:
                        CopyFileNamesBySortingPathToClipboard(clipboardDataPair, false);
                        break;
                    default:
                        break;
                }
                break;
            default:
                break;
        }
    }

    #endregion

    #region Original Format

    private async void CopyOriginallyToClipboard(ClipboardDataPair clipboardDataPair)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        var validObject = clipboardData.DataToValid();
        var dataType = clipboardData.DataType;
        if (validObject is not null)
        {
            var exception = await RetryActionOnSTAThreadAsync(() =>
            {
                switch (dataType)
                {
                    case DataType.PlainText:
                        Clipboard.SetText((string)validObject);
                        break;
                    case DataType.RichText:
                        Clipboard.SetText((string)validObject, TextDataFormat.Rtf);
                        break;
                    case DataType.Image:
                        Clipboard.SetImage((BitmapSource)validObject);
                        break;
                    case DataType.Files:
                        var paths = new StringCollection();
                        paths.AddRange((string[])validObject);
                        Clipboard.SetFileDropList(paths);
                        break;
                    default:
                        break;
                }
            });
            if (exception == null)
            {
                if (Settings.ShowNotification)
                {
                    Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                        Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard() +
                        StringUtils.CompressString(clipboardData.GetText(CultureInfo, validObject as string[]), StringMaxLength));
                }
            }
            else
            {
                Context.LogException(ClassName, "Copy to clipboard failed", exception);
                Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                        Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard_exception());
            }
        }
        else
        {
            switch (dataType)
            {
                case DataType.PlainText:
                case DataType.RichText:
                    Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                        Localize.flowlauncher_plugin_clipboardplus_text_data_invalid());
                    break;
                case DataType.Image:
                    Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                        Localize.flowlauncher_plugin_clipboardplus_image_data_invalid());
                    break;
                case DataType.Files:
                    Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                        Localize.flowlauncher_plugin_clipboardplus_files_data_invalid());
                    break;
            }
        }
    }

    #endregion

    #region Plain Text Format

    private async void CopyAsPlainTextToClipboard(ClipboardDataPair clipboardDataPair)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        var dataType = clipboardData.DataType;
        if (dataType != DataType.RichText)
        {
            return;
        }

        var validObject = clipboardData.PlainTextToValid();
        if (validObject is not null)
        {
            var exception = await RetryActionOnSTAThreadAsync(() =>
            {
                Clipboard.SetText(validObject);
            });
            if (exception == null)
            {
                if (Settings.ShowNotification)
                {
                    Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                        Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard() +
                        StringUtils.CompressString(clipboardData.GetText(CultureInfo), StringMaxLength));
                }
            }
            else
            {
                Context.LogException(ClassName, "Copy to clipboard failed", exception);
                Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                    Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard_exception());
            }
        }
        else
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                    Localize.flowlauncher_plugin_clipboardplus_text_data_invalid());
        }
    }

    #endregion

    #region Image File Format

    private async void CopyImageFileToClipboard(ClipboardDataPair clipboardDataPair)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        var dataType = clipboardData.DataType;
        if (dataType != DataType.Image)
        {
            return;
        }

        var cachePath = string.Empty;
        if ((!string.IsNullOrEmpty(clipboardData.CachedImagePath)) && File.Exists(clipboardData.CachedImagePath))
        {
            cachePath = clipboardData.CachedImagePath;
        }
        else
        {
            cachePath = FileUtils.SaveImageCache(clipboardData, PathHelper.ImageCachePath, PathHelper.TempCacheImageName);
        }

        if (!string.IsNullOrEmpty(cachePath))
        {
            var exception = await RetryActionOnSTAThreadAsync(() =>
            {
                Clipboard.SetFileDropList([cachePath]);
            });
            if (exception == null)
            {
                if (Settings.ShowNotification)
                {
                    Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                        Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard() +
                        StringUtils.CompressString(clipboardData.GetText(CultureInfo), StringMaxLength));
                }
            }
            else
            {
                Context.LogException(ClassName, "Copy to clipboard failed", exception);
                Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                    Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard_exception());
            }
        }
        else
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_image_data_invalid());
        }
    }

    #endregion

    #region Files Sorted Format

    private async void CopyBySortingNameToClipboard(ClipboardDataPair clipboardDataPair, bool ascend)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        var dataType = clipboardData.DataType;
        if (dataType != DataType.Files)
        {
            return;
        }

        if (clipboardData.Data is string[] filePaths)
        {
            var sortedFilePaths = ascend ? SortAscending(filePaths) : SortDescending(filePaths);
            var paths = new StringCollection();
            paths.AddRange(sortedFilePaths);
            var exception = await RetryActionOnSTAThreadAsync(() =>
            {
                Clipboard.SetFileDropList(paths);
            });
            if (exception == null)
            {
                if (Settings.ShowNotification)
                {
                    Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                        Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard() +
                        StringUtils.CompressString(clipboardData.GetText(CultureInfo, filePaths), StringMaxLength));
                }
            }
            else
            {
                Context.LogException(ClassName, "Copy to clipboard failed", exception);
                Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                    Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard_exception());
            }
        }
        else
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_text_data_invalid());
        }
    }

    private static string[] SortAscending(string[] strings)
    {
        return [.. strings.OrderBy(path => path, new NaturalStringComparer())];
    }

    private static string[] SortDescending(string[] strings)
    {
        return [.. strings.OrderByDescending(path => path, new NaturalStringComparer())];
    }

    private void CopyFileNamesBySortingPathToClipboard(ClipboardDataPair clipboardDataPair, bool ascend)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        if (clipboardData.DataType != DataType.Files)
        {
            return;
        }

        if (clipboardData.Data is not string[] filePaths)
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_files_data_invalid());
            return;
        }

        var sortedFilePaths = ascend ? SortAscending(filePaths) : SortDescending(filePaths);
        CopyFileNamesToClipboard(clipboardDataPair, sortedFilePaths);
    }

    private async void CopyFileNamesToClipboard(ClipboardDataPair _, string[] filePaths)
    {
        var fileNames = filePaths
            .Select(FileUtils.GetPathName);
        var text = string.Join(Environment.NewLine, fileNames);

        if (string.IsNullOrEmpty(text))
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_files_data_invalid());
            return;
        }

        var exception = await RetryActionOnSTAThreadAsync(() => Clipboard.SetText(text));
        if (exception == null)
        {
            if (Settings.ShowNotification)
            {
                Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                    Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard() +
                    StringUtils.CompressString(text, StringMaxLength));
            }
        }
        else
        {
            Context.LogException(ClassName, "Copy file names as text to clipboard failed", exception);
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard_exception());
        }
    }

    private void CopyFilePathsBySortingPathToClipboard(ClipboardDataPair clipboardDataPair, bool ascend)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        if (clipboardData.DataType != DataType.Files)
        {
            return;
        }

        if (clipboardData.Data is not string[] filePaths)
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_files_data_invalid());
            return;
        }

        var sortedFilePaths = ascend ? SortAscending(filePaths) : SortDescending(filePaths);
        CopyFilePathToClipboard(clipboardDataPair, sortedFilePaths);
    }

    public class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            // If both strings are null, they are considered equal
            if (x == null && y == null) return 0;

            // If one string is null and the other is not, the null string is considered less
            if (x == null) return -1;
            if (y == null) return 1;

            // Compare strings by processing numeric segments as numbers
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                // Skip leading non-numeric characters
                if (!char.IsDigit(x[i]) && !char.IsDigit(y[j]))
                {
                    if (x[i] != y[j]) return x[i] - y[j];
                    i++;
                    j++;
                    continue;
                }

                // Extract numeric segments
                var numX = ExtractNumber(x, ref i);
                var numY = ExtractNumber(y, ref j);

                // Compare numeric segments
                int result = numX.CompareTo(numY);
                if (result != 0) return result;
            }

            // Compare remaining non-numeric characters if one string is shorter
            return x.Length - y.Length;
        }

        private static int ExtractNumber(string s, ref int index)
        {
            int start = index;
            while (index < s.Length && char.IsDigit(s[index])) index++;
            if (start == index) return 0; // No number found, return 0
            return int.Parse(s[start..index]); // Extract and return the number
        }
    }

    #endregion

    #region Files Path Format

    private async void CopyFilePathToClipboard(ClipboardDataPair clipboardDataPair, string[] filePaths)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        if (filePaths.Length > 0 && filePaths.All(FileUtils.Exists))
        {
            var filePathText = string.Join(Environment.NewLine, filePaths);
            var exception = await RetryActionOnSTAThreadAsync(() =>
            {
                Clipboard.SetText(filePathText);
            });
            if (exception == null)
            {
                if (Settings.ShowNotification)
                {
                    Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                        Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard() +
                        StringUtils.CompressString(clipboardData.GetText(CultureInfo, filePaths), StringMaxLength));
                }
            }
            else
            {
                Context.LogException(ClassName, "Copy to clipboard failed", exception);
                Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                    Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard_exception());
            }
        }
        else
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_text_data_invalid());
        }
    }

    #endregion

    #region File Content Format

    private async void CopyFileContentToClipboard(ClipboardDataPair clipboardDataPair, string[] filePaths)
    {
        var clipboardData = clipboardDataPair.ClipboardData;
        var filePath = filePaths.FirstOrDefault();
        if (File.Exists(filePath))
        {
            var exception = await RetryActionOnSTAThreadAsync(() =>
            {
                if (FileUtils.IsImageFile(filePath))
                {
                    var image = filePath.ToImage();
                    Clipboard.SetImage(image);
                }
                else
                {
                    var text = File.ReadAllText(filePath);
                    Clipboard.SetText(text);
                }
            });
            if (exception == null)
            {
                if (Settings.ShowNotification)
                {
                    Context.ShowMsg(Localize.flowlauncher_plugin_clipboardplus_success(),
                        Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard() +
                        StringUtils.CompressString(clipboardData.GetText(CultureInfo, filePaths), StringMaxLength));
                }
            }
            else
            {
                Context.LogException(ClassName, "Copy to clipboard failed", exception);
                Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                    Localize.flowlauncher_plugin_clipboardplus_copy_to_clipboard_exception());
            }
        }
        else
        {
            Context.ShowMsgError(Localize.flowlauncher_plugin_clipboardplus_fail(),
                Localize.flowlauncher_plugin_clipboardplus_text_data_invalid());
        }
    }

    #endregion

    private static async Task<Exception?> RetryActionOnSTAThreadAsync(Action action)
    {
        for (int i = 0; i < ClipboardRetryTimes; i++)
        {
            try
            {
                await Win32Helper.StartSTATaskAsync(action).ConfigureAwait(false);
                break;
            }
            catch (Exception e)
            {
                if (i == ClipboardRetryTimes - 1)
                {
                    return e;
                }
                await Task.Delay(RetryInterval);
            }
        }
        return null;
    }

    #endregion

    #region IClipboardPlus Interface

    PluginInitContext? IClipboardPlus.Context => Context;

    bool IClipboardPlus.UseWindowsClipboardHistoryOnly => UseWindowsClipboardHistoryOnly;

    ObservableDataFormats IClipboardPlus.ObservableDataFormats => ObservableDataFormats;

    SqliteDatabase IClipboardPlus.Database => Database;

    ScoreHelper IClipboardPlus.ScoreHelper => ScoreHelper;

    ISettings IClipboardPlus.Settings => Settings;

    public ISettings LoadSettingJsonStorage() => Settings;

    public void SaveSettingJsonStorage()
    {
        RefreshAutomaticCleanup();
        Context!.API.SaveSettingJsonStorage<Settings>();
    }

    CultureInfo IClipboardPlus.CultureInfo => CultureInfo;

    public event EventHandler<CultureInfo>? CultureInfoChanged;

    public void AddExcludedPath(string appPath)
    {
        ClipboardMonitor?.AddExcludedPath(appPath);
    }

    public void RemoveExcludedPath(string appPath)
    {
        ClipboardMonitor?.RemoveExcludedPath(appPath);
    }

    public void ClearExcludedPath()
    {
        ClipboardMonitor?.ClearExcludedPath();
    }

    #endregion

    #region IAsyncDisposable Interface

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisposeAsync(true);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            Context.LogDebug(ClassName, $"Enter dispose");

            await cleanupCoordinator.StopAsync();

            if (Database != null)
            {
                await Database.DisposeAsync();
                Context.LogDebug(ClassName, $"Disposed DatabaseHelper");
            }

            if (ClipboardMonitor != null)
            {
                ClipboardMonitor.ClipboardChanged -= ClipboardMonitor_OnClipboardChanged;
                ClipboardMonitor.Dispose();
                Context.LogDebug(ClassName, $"Disposed ClipboardMonitor");
            }

            if (WindowsClipboardHelper != null)
            {
                DisableWindowsClipboardHelper(false);
                WindowsClipboardHelper.Dispose();
                Context.LogDebug(ClassName, $"Disposed WindowsClipboardHelper");
            }

            if (RecordsList != null)
            {
                ClearRecordsList();
                RecordsLock.Dispose();
                Context.LogDebug(ClassName, $"Disposed RecordsList");
            }

            try
            {
                await RetryActionOnSTAThreadAsync(Clipboard.Flush);
            }
            catch (Exception e)
            {
                Context.LogException(ClassName, "Flush clipboard failed", e);
            }

            GarbageCollect();
            Context.LogDebug(ClassName, $"Finish dispose");

            _disposed = true;
        }
    }

    #endregion

    #region Garbage Collect

    private static void GarbageCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    #endregion
}
