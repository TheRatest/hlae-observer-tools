using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Video;
using HlaeObsTools.Services.Video.Shared;
using HlaeObsTools.Services.WebSocket;

namespace HlaeObsTools.ViewModels.Docks;

public enum CampathPopulateSource
{
    Folder,
    Files
}

public class CampathsDockViewModel : Tool
{
    private readonly CampathStorage _storage = new();
    private readonly DelegateCommand _addProfileCommand;
    private readonly DelegateCommand _removeProfileCommand;
    private readonly DelegateCommand _addCampathCommand;
    private readonly DelegateCommand _populateFromFolderCommand;
    private readonly DelegateCommand _addGroupCommand;
    private readonly DelegateCommand _removeCampathCommand;
    private readonly DelegateCommand _renameCampathCommand;
    private readonly DelegateCommand _browseCampathCommand;
    private readonly DelegateCommand _browseImageCommand;
    private readonly DelegateCommand _screenShotCommand;
    private readonly DelegateCommand _setOffsetCommand;
    private readonly DelegateCommand _deleteGroupCommand;
    private readonly DelegateCommand _toggleGroupModeCommand;
    private readonly DelegateCommand _viewGroupCommand;
    private HlaeWebSocketClient? _webSocketClient;
    private readonly Dictionary<Guid, int> _groupPlaybackIndex = new();
    private readonly Random _random = new();
    private CampathItemViewModel? _currentlyPlayingCampath;

    private ObservableCollection<CampathProfileViewModel> _profiles = new();
    private CampathProfileViewModel? _selectedProfile;
    private double _scale = 1.0;

    public CampathsDockViewModel()
    {
        Title = "Campaths";
        CanClose = false;
        CanFloat = true;
        CanPin = true;

        _addProfileCommand = new DelegateCommand(async _ => await AddProfileAsync());
        _removeProfileCommand = new DelegateCommand(_ => { RemoveProfile(); return Task.CompletedTask; }, _ => SelectedProfile != null);
        _addCampathCommand = new DelegateCommand(async _ => await AddCampathAsync(), _ => SelectedProfile != null);
        _populateFromFolderCommand = new DelegateCommand(async _ => await PopulateFromFolderAsync(), _ => SelectedProfile != null);
        _addGroupCommand = new DelegateCommand(async _ => await AddGroupAsync(), _ => SelectedProfile != null);
        _removeCampathCommand = new DelegateCommand(param => { RemoveCampath(param as CampathItemViewModel); return Task.CompletedTask; }, _ => SelectedProfile != null);
        _renameCampathCommand = new DelegateCommand(async param => await RenameCampathAsync(param as CampathItemViewModel), _ => SelectedProfile != null);
        _browseCampathCommand = new DelegateCommand(async param => await BrowseCampathAsync(param as CampathItemViewModel), _ => SelectedProfile != null);
        _browseImageCommand = new DelegateCommand(async param => await BrowseImageAsync(param as CampathItemViewModel), _ => SelectedProfile != null);
        _screenShotCommand = new DelegateCommand(async param => await ScreenShotCampathAsync(param as CampathItemViewModel), _ => SelectedProfile != null);
        _setOffsetCommand = new DelegateCommand(async param => await SetCampathOffsetAsync(param as CampathItemViewModel), _ => SelectedProfile != null);
        _deleteGroupCommand = new DelegateCommand(param => { DeleteGroup(param as CampathGroupViewModel); return Task.CompletedTask; }, _ => SelectedProfile != null);
        _toggleGroupModeCommand = new DelegateCommand(param => { ToggleGroupMode(param as CampathGroupViewModel); return Task.CompletedTask; }, _ => SelectedProfile != null);
        _viewGroupCommand = new DelegateCommand(param => { ViewGroupRequested?.Invoke(this, param as CampathGroupViewModel); return Task.CompletedTask; }, _ => SelectedProfile != null);

        Load();
    }

    public ObservableCollection<CampathProfileViewModel> Profiles
    {
        get => _profiles;
        set => SetProperty(ref _profiles, value);
    }

    public CampathProfileViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value != null)
            {
                Scale = value.Scale;
            }

            _removeProfileCommand.RaiseCanExecuteChanged();
            _addCampathCommand.RaiseCanExecuteChanged();
            _populateFromFolderCommand.RaiseCanExecuteChanged();
            _addGroupCommand.RaiseCanExecuteChanged();
        }
    }

    public double Scale
    {
        get => _scale;
        set
        {
            if (SetProperty(ref _scale, value) && SelectedProfile != null)
            {
                SelectedProfile.Scale = value;
                Save();
            }
        }
    }

    public ICommand AddProfileCommand => _addProfileCommand;
    public ICommand RemoveProfileCommand => _removeProfileCommand;
    public ICommand AddCampathCommand => _addCampathCommand;
    public ICommand PopulateFromFolderCommand => _populateFromFolderCommand;
    public ICommand AddGroupCommand => _addGroupCommand;
    public ICommand RemoveCampathCommand => _removeCampathCommand;
    public ICommand RenameCampathCommand => _renameCampathCommand;
    public ICommand BrowseCampathCommand => _browseCampathCommand;
    public ICommand BrowseImageCommand => _browseImageCommand;
    public ICommand ScreenShotCommand => _screenShotCommand;
    public ICommand SetOffsetCommand => _setOffsetCommand;
    public ICommand DeleteGroupCommand => _deleteGroupCommand;
    public ICommand ToggleGroupModeCommand => _toggleGroupModeCommand;
    public ICommand ViewGroupCommand => _viewGroupCommand;

    public async Task AddProfileAsync()
    {
        var name = await PromptAsync("Profile Name", "Enter a profile name:", 250, 150);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var profileVm = new CampathProfileViewModel(new CampathProfileData { Name = name });
        Profiles.Add(profileVm);
        SelectedProfile = profileVm;
        Save();
    }

    public void RemoveProfile()
    {
        if (SelectedProfile == null)
            return;

        var toRemove = SelectedProfile;
        Profiles.Remove(toRemove);
        SelectedProfile = Profiles.FirstOrDefault();
        Save();
    }

    public async Task AddCampathAsync()
    {
        if (SelectedProfile == null)
            return;

        var name = await PromptAsync("Campath Name", "Enter a campath name:", 250, 150);
        if (string.IsNullOrWhiteSpace(name))
            return;

        SelectedProfile.AddCampath(name);
        Save();
    }

    public async Task PopulateFromFolderAsync()
    {
        if (SelectedProfile == null)
            return;

        var source = await SelectPopulateSourceAsync();
        if (source == null)
            return;

        string[] files;
        if (source == CampathPopulateSource.Folder)
        {
            var folder = await BrowseFolderAsync("Select folder containing campath files");
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;

            files = GetCampathFilesFromFolder(folder);
        }
        else
        {
            var selectedFiles = await BrowseFilesAsync("Select campath files");
            files = selectedFiles?.Where(IsSupportedCampathFile).ToArray() ?? Array.Empty<string>();
        }

        if (files.Length == 0)
            return;

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            SelectedProfile.AddCampath(name, file);
        }

        Save();
    }

    private static string[] GetCampathFilesFromFolder(string folder)
    {
        return Directory
            .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedCampathFile)
            .ToArray();
    }

    private static bool IsSupportedCampathFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
            return true;

        return extension.Equals(".campath", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cam", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".path", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    public async Task AddGroupAsync()
    {
        if (SelectedProfile == null)
            return;

        var name = await PromptAsync("Group Name", "Enter a group name:", 250, 150);
        if (string.IsNullOrWhiteSpace(name))
            return;

        SelectedProfile.AddGroup(name);
        Save();
    }

    public void Save()
    {
        var data = new CampathStorageData
        {
            Profiles = Profiles.Select(p => p.ToData()).ToList(),
            SelectedProfileId = SelectedProfile?.Id
        };

        _storage.Save(data);
    }

    private void Load()
    {
        var data = _storage.Load();
        Profiles = new ObservableCollection<CampathProfileViewModel>(data.Profiles.Select(p => new CampathProfileViewModel(p)));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == data.SelectedProfileId) ?? Profiles.FirstOrDefault();
        if (SelectedProfile != null)
        {
            Scale = SelectedProfile.Scale;
        }
    }

    // The view wires these to actual UI dialogs to avoid viewmodel knowing about UI
    public Func<string, string, int, int, Task<string?>> PromptAsync { get; set; } = (_, _, _, _) => Task.FromResult<string?>(null);
    public Func<Task<CampathPopulateSource?>> SelectPopulateSourceAsync { get; set; } = () => Task.FromResult<CampathPopulateSource?>(CampathPopulateSource.Folder);
    public Func<string, Task<string?>> BrowseFileAsync { get; set; } = _ => Task.FromResult<string?>(null);
    public Func<string, Task<IEnumerable<string>?>> BrowseFilesAsync { get; set; } = _ => Task.FromResult<IEnumerable<string>?>(null);
    public Func<string, Task<string?>> BrowseFolderAsync { get; set; } = _ => Task.FromResult<string?>(null);

    public event EventHandler<CampathGroupViewModel?>? ViewGroupRequested;
    public HlaeWebSocketClient? WebSocketClient => _webSocketClient;

    public void SetWebSocketClient(HlaeWebSocketClient client)
    {
        _webSocketClient = client;
    }

    public void RemoveCampath(CampathItemViewModel? item)
    {
        if (item == null || SelectedProfile == null)
            return;

        SelectedProfile.RemoveCampath(item.Id);
        Save();
    }

    public async Task RenameCampathAsync(CampathItemViewModel? item)
    {
        if (item == null)
            return;

        var name = await PromptAsync("Rename Campath", "Enter a new name:", 250, 150);
        if (string.IsNullOrWhiteSpace(name))
            return;

        item.Name = name;
        Save();
    }

    public async Task BrowseCampathAsync(CampathItemViewModel? item)
    {
        if (item == null)
            return;

        var path = await BrowseFileAsync("Select campath file");
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.FilePath = path;
            Save();
        }
    }

    public async Task BrowseImageAsync(CampathItemViewModel? item)
    {
        if (item == null)
            return;

        var path = await BrowseFileAsync("Select image file");
        if (!string.IsNullOrWhiteSpace(path))
        {
            item.ImagePath = path;
            Save();
        }
    }

    public async Task SetCampathOffsetAsync(CampathItemViewModel? item)
    {
        if (item == null)
            return;

        var input = await PromptAsync("Campath Offset", "Offset(seconds):", 250, 150);
        if (string.IsNullOrWhiteSpace(input))
            return;

        if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) ||
            double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out offset))
        {
            item.Offset = Math.Max(0, offset);
            Save();
        }
    }

    public async Task ScreenShotCampathAsync(CampathItemViewModel? item)
    {
        if (item == null)
            return;

        if (string.IsNullOrWhiteSpace(item.FilePath))
        {
            Console.WriteLine($"Campath '{item.Name}' has no file path set.");
            return;
        }

        var targetPath = Path.ChangeExtension(item.FilePath, ".png");
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
        {
            Console.WriteLine($"Campath '{item.Name}' has invalid path '{item.FilePath}'.");
            return;
        }

        var frame = await CaptureSharedTextureFrameAsync();
        if (frame == null)
        {
            Console.WriteLine("Failed to capture shared texture frame.");
            return;
        }

        var saved = await Task.Run(() => SaveFrameToPng(frame, targetPath));
        if (!saved)
        {
            Console.WriteLine($"Failed to save campath screenshot to '{targetPath}'.");
            return;
        }

        UpdateCampathImage(item, targetPath);
    }

    private void UpdateCampathImage(CampathItemViewModel item, string imagePath)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (string.Equals(item.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                item.RefreshThumbnail();
            }
            else
            {
                item.ImagePath = imagePath;
            }
            Save();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (string.Equals(item.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
            {
                item.RefreshThumbnail();
            }
            else
            {
                item.ImagePath = imagePath;
            }
            Save();
        }, DispatcherPriority.Background);
    }

    private static bool SaveFrameToPng(VideoFrame frame, string path)
    {
        try
        {
            using var bitmap = CreateBitmapFromFrame(frame);
            using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            bitmap.Save(stream);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SaveFrameToPng error: {ex.Message}");
            return false;
        }
    }

    private static WriteableBitmap CreateBitmapFromFrame(VideoFrame frame)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(frame.Width, frame.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888);

        using (var buffer = bitmap.Lock())
        {
            unsafe
            {
                var dest = (byte*)buffer.Address;
                var destStride = buffer.RowBytes;

                for (int y = 0; y < frame.Height; y++)
                {
                    int srcOffset = y * frame.Stride;
                    int destOffset = y * destStride;

                    Marshal.Copy(
                        frame.Data,
                        srcOffset,
                        (IntPtr)(dest + destOffset),
                        Math.Min(frame.Stride, destStride));
                }
            }
        }

        return bitmap;
    }

    private static async Task<VideoFrame?> CaptureSharedTextureFrameAsync()
    {
        using var source = new SharedTextureVideoSource();
        var tcs = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, VideoFrame frame)
        {
            var dataCopy = new byte[frame.Stride * frame.Height];
            Buffer.BlockCopy(frame.Data, 0, dataCopy, 0, dataCopy.Length);

            tcs.TrySetResult(new VideoFrame
            {
                Data = dataCopy,
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Stride,
                Timestamp = frame.Timestamp,
                SourceTimestampUs = frame.SourceTimestampUs,
                ReceivedTimestampUs = frame.ReceivedTimestampUs
            });
        }

        source.FrameReceived += Handler;

        try
        {
            source.Start();
        }
        catch (Exception ex)
        {
            source.FrameReceived -= Handler;
            Console.WriteLine($"SharedTextureVideoSource start failed: {ex.Message}");
            return null;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Shared texture capture timed out.");
            return null;
        }
        finally
        {
            source.FrameReceived -= Handler;
            source.Stop();
        }
    }

    public void DeleteGroup(CampathGroupViewModel? group)
    {
        if (group == null || SelectedProfile == null)
            return;

        SelectedProfile.Groups.Remove(group);
        Save();
    }

    public void ToggleGroupMode(CampathGroupViewModel? group)
    {
        if (group == null)
            return;

        group.ToggleMode();
        Save();
    }

    public void MoveCampath(CampathItemViewModel source, CampathItemViewModel? target)
    {
        if (SelectedProfile == null)
            return;

        var campaths = SelectedProfile.Campaths;
        var srcIndex = campaths.IndexOf(source);
        if (srcIndex < 0)
            return;

        if (target == null)
        {
            campaths.RemoveAt(srcIndex);
            campaths.Add(source);
            Save();
            return;
        }

        var targetIndex = campaths.IndexOf(target);
        if (targetIndex < 0)
            return;

        var moveDown = srcIndex < targetIndex;

        campaths.RemoveAt(srcIndex);

        if (moveDown)
            targetIndex--; // target shifted after removal

        var insertIndex = moveDown ? targetIndex + 1 : targetIndex;
        if (insertIndex < 0) insertIndex = 0;
        if (insertIndex > campaths.Count) insertIndex = campaths.Count;

        campaths.Insert(insertIndex, source);
        Save();
    }

    public void AddCampathToGroup(CampathItemViewModel campath, CampathGroupViewModel group)
    {
        if (SelectedProfile == null)
            return;

        group.AddCampath(campath.Id);
        Save();
    }

    public async Task PlayCampathAsync(CampathItemViewModel? campath)
    {
        if (campath == null)
            return;

        if (string.IsNullOrWhiteSpace(campath.FilePath))
        {
            Console.WriteLine($"Campath '{campath.Name}' has no file path set.");
            return;
        }

        if (_webSocketClient == null)
        {
            Console.WriteLine("WebSocket client not available for campath playback.");
            return;
        }

        // Stop the currently playing campath if any
        _currentlyPlayingCampath?.StopPlayback();

        // Parse campath file to get duration
        var campathFile = CampathFileParser.Parse(campath.FilePath);
        if (campathFile != null && campathFile.Points.Count > 0)
        {
            var firstTime = campathFile.Points[0].Time;
            var lastTime = campathFile.Points[campathFile.Points.Count - 1].Time;
            var duration = lastTime - firstTime;
            var effectiveDuration = Math.Max(0.0, duration - campath.Offset);

            if (effectiveDuration > 0)
            {
                campath.StartPlayback(effectiveDuration);
                _currentlyPlayingCampath = campath;
            }
        }

        await _webSocketClient.SendCampathPlayAsync(campath.FilePath, campath.Offset);
    }

    public async Task PlayCampathGroupAsync(CampathGroupViewModel? group)
    {
        if (group == null || SelectedProfile == null)
            return;

        if (_webSocketClient == null)
        {
            Console.WriteLine("WebSocket client not available for campath playback.");
            return;
        }

        var campathLookup = SelectedProfile.Campaths.ToDictionary(c => c.Id, c => c);
        var available = group.CampathIds
            .Select(id => campathLookup.TryGetValue(id, out var c) ? c : null)
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.FilePath))
            .Cast<CampathItemViewModel>()
            .ToList();

        if (available.Count == 0)
        {
            Console.WriteLine($"Group '{group.Name}' has no playable campaths.");
            return;
        }

        CampathItemViewModel selected;
        if (group.Mode == CampathGroupMode.Seq)
        {
            var nextIndex = 0;
            if (_groupPlaybackIndex.TryGetValue(group.Id, out var lastIndex))
            {
                nextIndex = (lastIndex + 1) % available.Count;
            }
            _groupPlaybackIndex[group.Id] = nextIndex;
            selected = available[nextIndex];
        }
        else
        {
            selected = available[_random.Next(available.Count)];
        }

        // Stop the currently playing campath if any
        _currentlyPlayingCampath?.StopPlayback();

        // Parse campath file to get duration
        var campathFile = CampathFileParser.Parse(selected.FilePath!);
        if (campathFile != null && campathFile.Points.Count > 0)
        {
            var firstTime = campathFile.Points[0].Time;
            var lastTime = campathFile.Points[campathFile.Points.Count - 1].Time;
            var duration = lastTime - firstTime;
            var effectiveDuration = Math.Max(0.0, duration - selected.Offset);

            if (effectiveDuration > 0)
            {
                selected.StartPlayback(effectiveDuration);
                _currentlyPlayingCampath = selected;
            }
        }

        await _webSocketClient.SendCampathPlayAsync(selected.FilePath!, selected.Offset);
    }

    public void MoveGroup(CampathGroupViewModel source, CampathGroupViewModel? target)
    {
        if (SelectedProfile == null)
            return;

        var groups = SelectedProfile.Groups;
        var srcIndex = groups.IndexOf(source);
        if (srcIndex < 0)
            return;

        if (target == null)
        {
            groups.RemoveAt(srcIndex);
            groups.Add(source);
            Save();
            return;
        }

        var targetIndex = groups.IndexOf(target);
        if (targetIndex < 0)
            return;

        var moveDown = srcIndex < targetIndex;
        groups.RemoveAt(srcIndex);
        if (moveDown)
            targetIndex--;

        var insertIndex = moveDown ? targetIndex + 1 : targetIndex;
        if (insertIndex < 0) insertIndex = 0;
        if (insertIndex > groups.Count) insertIndex = groups.Count;

        groups.Insert(insertIndex, source);
        Save();
    }
}

public class CampathProfileViewModel : ViewModelBase
{
    private readonly ObservableCollection<CampathItemViewModel> _campaths;
    private readonly ObservableCollection<CampathGroupViewModel> _groups;
    private string _name;
    private double _scale;

    public CampathProfileViewModel(CampathProfileData data)
    {
        Id = data.Id;
        _name = data.Name;
        _scale = data.Scale;
        _campaths = new ObservableCollection<CampathItemViewModel>(data.Campaths.Select(c => new CampathItemViewModel(c)));
        _groups = new ObservableCollection<CampathGroupViewModel>(data.Groups.Select(g => new CampathGroupViewModel(g)));
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public double Scale
    {
        get => _scale;
        set => SetProperty(ref _scale, value);
    }

    public ObservableCollection<CampathItemViewModel> Campaths => _campaths;
    public ObservableCollection<CampathGroupViewModel> Groups => _groups;

    public void AddCampath(string name, string? filePath = null)
    {
        Campaths.Add(new CampathItemViewModel(new CampathData
        {
            Name = name,
            FilePath = filePath
        }));
    }

    public void RemoveCampath(Guid id)
    {
        var item = Campaths.FirstOrDefault(c => c.Id == id);
        if (item != null)
        {
            Campaths.Remove(item);
            foreach (var group in Groups)
            {
                group.RemoveCampath(id);
            }
        }
    }

    public void AddGroup(string name)
    {
        Groups.Add(new CampathGroupViewModel(new CampathGroupData { Name = name }));
    }

    public CampathProfileData ToData()
    {
        return new CampathProfileData
        {
            Id = Id,
            Name = Name,
            Scale = Scale,
            Campaths = Campaths.Select(c => c.ToData()).ToList(),
            Groups = Groups.Select(g => g.ToData()).ToList()
        };
    }

    public void MoveCampath(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= Campaths.Count || newIndex >= Campaths.Count)
            return;

        var item = Campaths[oldIndex];
        Campaths.RemoveAt(oldIndex);
        Campaths.Insert(newIndex, item);
    }

    public void MoveGroup(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= Groups.Count || newIndex >= Groups.Count)
            return;

        var item = Groups[oldIndex];
        Groups.RemoveAt(oldIndex);
        Groups.Insert(newIndex, item);
    }
}

public class CampathItemViewModel : ViewModelBase
{
    private string _name;
    private string? _filePath;
    private string? _imagePath;
    private Avalonia.Media.Imaging.Bitmap? _thumbnail;
    private double _offset;
    private double _playbackProgress;
    private bool _isPlaying;
    private System.Timers.Timer? _progressTimer;
    private DateTime _playbackStartTime;
    private double _campathDuration;

    public CampathItemViewModel(CampathData data)
    {
        Id = data.Id;
        _name = data.Name;
        _filePath = data.FilePath;
        _imagePath = data.ImagePath;
        _offset = data.Offset;
        UpdateThumbnail();
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string? FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public string? ImagePath
    {
        get => _imagePath;
        set
        {
            if (SetProperty(ref _imagePath, value))
            {
                UpdateThumbnail();
            }
        }
    }

    public Avalonia.Media.Imaging.Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public double Offset
    {
        get => _offset;
        set => SetProperty(ref _offset, value);
    }

    public double PlaybackProgress
    {
        get => _playbackProgress;
        private set => SetProperty(ref _playbackProgress, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public CampathData ToData() => new()
    {
        Id = Id,
        Name = Name,
        FilePath = FilePath,
        ImagePath = ImagePath,
        Offset = Offset
    };

    public void StartPlayback(double duration)
    {
        if (duration <= 0)
            return;

        _campathDuration = duration;
        _playbackStartTime = DateTime.UtcNow;
        PlaybackProgress = 0;
        IsPlaying = true;

        _progressTimer?.Stop();
        _progressTimer?.Dispose();
        _progressTimer = new System.Timers.Timer(33); // Update at ~30fps
        _progressTimer.Elapsed += (s, e) => UpdateProgress();
        _progressTimer.Start();
    }

    public void StopPlayback()
    {
        IsPlaying = false;
        PlaybackProgress = 0;
        _progressTimer?.Stop();
        _progressTimer?.Dispose();
        _progressTimer = null;
    }

    private void UpdateProgress()
    {
        if (!IsPlaying)
            return;

        var elapsed = (DateTime.UtcNow - _playbackStartTime).TotalSeconds;
        var progress = Math.Min(elapsed / _campathDuration, 1.0);

        PlaybackProgress = progress;

        if (progress >= 1.0)
        {
            StopPlayback();
        }
    }

    private void UpdateThumbnail()
    {
        try
        {
            Thumbnail?.Dispose();
            Thumbnail = null;

            if (!string.IsNullOrWhiteSpace(_imagePath) && File.Exists(_imagePath))
            {
                using var stream = File.OpenRead(_imagePath);
                Thumbnail = new Avalonia.Media.Imaging.Bitmap(stream);
            }
        }
        catch
        {
            Thumbnail = null;
        }
    }

    public void RefreshThumbnail()
    {
        UpdateThumbnail();
    }
}

public class CampathGroupViewModel : ViewModelBase
{
    private string _name;
    private CampathGroupMode _mode;
    private readonly ObservableCollection<Guid> _campathIds;

    public CampathGroupViewModel(CampathGroupData data)
    {
        Id = data.Id;
        _name = data.Name;
        _mode = data.Mode;
        _campathIds = new ObservableCollection<Guid>(data.CampathIds);
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public CampathGroupMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, value);
    }

    public ObservableCollection<Guid> CampathIds => _campathIds;

    public void ToggleMode()
    {
        Mode = Mode == CampathGroupMode.Seq ? CampathGroupMode.Rnd : CampathGroupMode.Seq;
    }

    public void AddCampath(Guid id)
    {
        if (!_campathIds.Contains(id))
            _campathIds.Add(id);
    }

    public void RemoveCampath(Guid id)
    {
        if (_campathIds.Contains(id))
            _campathIds.Remove(id);
    }

    public void MoveCampath(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || oldIndex < 0 || newIndex < 0 || oldIndex >= _campathIds.Count || newIndex >= _campathIds.Count)
            return;

        var item = _campathIds[oldIndex];
        _campathIds.RemoveAt(oldIndex);
        _campathIds.Insert(newIndex, item);
    }

    public CampathGroupData ToData() => new()
    {
        Id = Id,
        Name = Name,
        Mode = Mode,
        CampathIds = _campathIds.ToList()
    };
}
