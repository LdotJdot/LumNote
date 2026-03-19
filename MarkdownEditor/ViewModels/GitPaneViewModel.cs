using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using MarkdownEditor.Models;
using MarkdownEditor.Services;

namespace MarkdownEditor.ViewModels;

/// <summary>版本管理面板 ViewModel：仓库选择、变更列表、暂存、提交、分支、拉取/推送。</summary>
public sealed class GitPaneViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<string>> _getWorkspaceRoots;
    private List<GitRepositoryInfo> _repositories = [];
    private GitRepositoryInfo? _selectedRepository;
    private string _currentBranchName = "";
    private string? _selectedBranchName;
    private List<string> _branchNames = [];
    private string _commitMessage = "";
    private bool _isRefreshing;
    private string? _statusMessage;
    private string? _pullPushError;

    public GitPaneViewModel(Func<IReadOnlyList<string>> getWorkspaceRoots)
    {
        _getWorkspaceRoots = getWorkspaceRoots;
        Changes = new ObservableCollection<GitChangeItem>();
        RefreshRepositoriesCommand = new RelayCommand(RefreshRepositories);
        InitRepositoryCommand = new RelayCommand(InitRepository);
        RefreshStatusCommand = new RelayCommand(RefreshStatus);
        StageAllCommand = new RelayCommand(StageAll);
        UnstageAllCommand = new RelayCommand(UnstageAll);
        StageSelectedCommand = new RelayCommand(StageSelected);
        UnstageSelectedCommand = new RelayCommand(UnstageSelected);
        CommitCommand = new RelayCommand(Commit);
        CreateBranchCommand = new RelayCommand(CreateBranch);
        PullCommand = new RelayCommand(PullAsync);
        PushCommand = new RelayCommand(PushAsync);
    }

    /// <summary>拉取/推送时用于获取凭证；由视图设置为弹窗等。若未设置则使用空凭证（可能失败）。</summary>
    public Func<string?, string?, (string? username, string? password)?>? CredentialsProvider { get; set; }

    public ObservableCollection<GitChangeItem> Changes { get; }

    /// <summary>当前文件的时间线（提交历史）；由 <see cref="LoadTimelineForFile"/> 填充。</summary>
    public ObservableCollection<GitCommitItem> FileHistory { get; } = new();

    /// <summary>当前用于时间线的文件路径（绝对路径）；设为 null 或非仓库内文件时清空 FileHistory。</summary>
    public string? TimelineFilePath
    {
        get => _timelineFilePath;
        set
        {
            if (SetProperty(ref _timelineFilePath, value))
                LoadTimelineForFile();
        }
    }
    private string? _timelineFilePath;

    /// <summary>根据 <see cref="TimelineFilePath"/> 与当前所选仓库加载该文件的提交历史到 <see cref="FileHistory"/>。</summary>
    public void LoadTimelineForFile()
    {
        FileHistory.Clear();
        if (string.IsNullOrWhiteSpace(_timelineFilePath) || _selectedRepository == null) return;
        var repoRoot = GitService.FindRepositoryRootForPath(_timelineFilePath);
        if (string.IsNullOrEmpty(repoRoot) || !string.Equals(repoRoot, _selectedRepository.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) return;
        var list = GitService.GetFileLog(repoRoot, _timelineFilePath);
        foreach (var item in list)
            FileHistory.Add(item);
    }

    /// <summary>用户点击时间线中某条提交时，请求查看该版本内容或与当前比对。参数：commitSha。</summary>
    public event EventHandler<string>? ViewCommitRequested;

    /// <summary>当前选中的变更项（单条，用于“暂存所选”/“取消暂存所选”）。</summary>
    public GitChangeItem? SelectedChangeItem
    {
        get => _selectedChangeItem;
        set => SetProperty(ref _selectedChangeItem, value);
    }
    private GitChangeItem? _selectedChangeItem;

    public IReadOnlyList<GitRepositoryInfo> Repositories => _repositories;
    public bool HasMultipleRepositories => _repositories.Count > 1;
    public bool HasRepositories => _repositories.Count > 0;

    public GitRepositoryInfo? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (SetProperty(ref _selectedRepository, value))
            {
                OnPropertyChanged(nameof(SelectedRepositoryIsInitialized));
                OnPropertyChanged(nameof(ShowInitRepositoryPanel));
                RefreshStatus();
                LoadTimelineForFile();
                OnPropertyChanged(nameof(CurrentBranchName));
                OnPropertyChanged(nameof(BranchNames));
            }
        }
    }

    /// <summary>当前所选目录是否已为 Git 仓库（否则显示“初始化”入口）。</summary>
    public bool SelectedRepositoryIsInitialized => _selectedRepository?.IsInitialized ?? false;

    /// <summary>是否显示“初始化 Git 仓库”面板（有工作区且当前所选目录未初始化）。</summary>
    public bool ShowInitRepositoryPanel => HasRepositories && _selectedRepository != null && !_selectedRepository.IsInitialized;

    public string CurrentBranchName
    {
        get => _currentBranchName;
        private set => SetProperty(ref _currentBranchName, value);
    }

    public IReadOnlyList<string> BranchNames => _branchNames;

    /// <summary>当前选中的分支（ComboBox 绑定）；设置时执行切换分支。</summary>
    public string? SelectedBranchName
    {
        get => _selectedBranchName ?? _currentBranchName;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _selectedBranchName) return;
            if (SetProperty(ref _selectedBranchName, value))
                CheckoutBranch(value);
        }
    }

    /// <summary>“更改数(n)” 文案。</summary>
    public string ChangesCountText => $"更改数({Changes.Count})";

    public string CommitMessage
    {
        get => _commitMessage;
        set => SetProperty(ref _commitMessage, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string? PullPushError
    {
        get => _pullPushError;
        private set => SetProperty(ref _pullPushError, value);
    }

    private bool HasStagedChanges => Changes.Any(c => c.IsStaged);

    public ICommand RefreshRepositoriesCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand StageAllCommand { get; }
    public ICommand UnstageAllCommand { get; }
    public ICommand StageSelectedCommand { get; }
    public ICommand UnstageSelectedCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand CreateBranchCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand PushCommand { get; }

    public void RefreshRepositories()
    {
        var roots = _getWorkspaceRoots();
        var prevPath = _selectedRepository?.WorkingDirectory;
        _repositories = GitService.DiscoverRepositories(roots).ToList();
        OnPropertyChanged(nameof(Repositories));
        OnPropertyChanged(nameof(HasMultipleRepositories));
        OnPropertyChanged(nameof(HasRepositories));
        OnPropertyChanged(nameof(ShowInitRepositoryPanel));
        if (_repositories.Count == 0)
            SelectedRepository = null;
        else if (!string.IsNullOrEmpty(prevPath))
        {
            var match = _repositories.FirstOrDefault(r => string.Equals(r.WorkingDirectory, prevPath, StringComparison.OrdinalIgnoreCase));
            SelectedRepository = match ?? _repositories[0];
        }
        else
            SelectedRepository = _repositories[0];
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        if (_selectedRepository == null)
        {
            Changes.Clear();
            CurrentBranchName = "";
            _branchNames = [];
            OnPropertyChanged(nameof(BranchNames));
            StatusMessage = "未选择仓库";
            return;
        }
        if (!_selectedRepository.IsInitialized)
        {
            Changes.Clear();
            CurrentBranchName = "";
            _branchNames = [];
            OnPropertyChanged(nameof(BranchNames));
            StatusMessage = "此文件夹尚未初始化 Git，可点击下方按钮初始化。";
            return;
        }
        IsRefreshing = true;
        StatusMessage = "刷新中…";
        Task.Run(() =>
        {
            var status = GitService.GetStatus(_selectedRepository.WorkingDirectory);
            var branch = GitService.GetCurrentBranchName(_selectedRepository.WorkingDirectory);
            var branches = GitService.GetLocalBranchNames(_selectedRepository.WorkingDirectory);
            Dispatcher.UIThread.Post(() =>
            {
                Changes.Clear();
                foreach (var item in status)
                    Changes.Add(item);
                CurrentBranchName = branch ?? "";
                _branchNames = branches.ToList();
                _selectedBranchName = branch;
                OnPropertyChanged(nameof(BranchNames));
                OnPropertyChanged(nameof(SelectedBranchName));
                OnPropertyChanged(nameof(ChangesCountText));
                IsRefreshing = false;
                StatusMessage = status.Count > 0 ? $"共 {status.Count} 项更改" : "无更改";
            });
        });
    }

    public ICommand InitRepositoryCommand { get; }

    private void InitRepository()
    {
        if (_selectedRepository == null || _selectedRepository.IsInitialized) return;
        if (GitService.InitRepository(_selectedRepository.WorkingDirectory))
        {
            RefreshRepositories();
            StatusMessage = "已在此文件夹创建 Git 仓库";
        }
        else
        {
            StatusMessage = "初始化失败";
        }
    }

    private void StageAll()
    {
        if (_selectedRepository == null) return;
        var paths = Changes.Select(c => c.FullPath).ToList();
        if (GitService.Stage(_selectedRepository.WorkingDirectory, paths))
            RefreshStatus();
        else
            StatusMessage = "暂存失败";
    }

    private void UnstageAll()
    {
        if (_selectedRepository == null) return;
        var paths = Changes.Select(c => c.FullPath).ToList();
        if (GitService.Unstage(_selectedRepository.WorkingDirectory, paths))
            RefreshStatus();
        else
            StatusMessage = "取消暂存失败";
    }

    private void StageSelected()
    {
        if (_selectedRepository == null || _selectedChangeItem == null) return;
        if (GitService.Stage(_selectedRepository.WorkingDirectory, new[] { _selectedChangeItem.FullPath }))
            RefreshStatus();
    }

    private void UnstageSelected()
    {
        if (_selectedRepository == null || _selectedChangeItem == null) return;
        if (GitService.Unstage(_selectedRepository.WorkingDirectory, new[] { _selectedChangeItem.FullPath }))
            RefreshStatus();
    }

    /// <summary>智能提交（Smart Commit）：提交前自动暂存所有未暂存的更改，再执行提交。</summary>
    private void Commit()
    {
        if (_selectedRepository == null || string.IsNullOrWhiteSpace(CommitMessage)) return;
        var paths = Changes.Select(c => c.FullPath).ToList();
        if (paths.Count > 0)
            GitService.Stage(_selectedRepository.WorkingDirectory, paths);
        var (success, error) = GitService.Commit(_selectedRepository.WorkingDirectory, CommitMessage.Trim());
        if (success)
        {
            CommitMessage = "";
            OnPropertyChanged(nameof(CommitMessage));
            RefreshStatus();
            StatusMessage = "已保存版本";
        }
        else
        {
            StatusMessage = error ?? "提交失败";
        }
    }

    private void CreateBranch()
    {
        if (_selectedRepository == null) return;
        CreateBranchRequested?.Invoke(this, _selectedRepository.WorkingDirectory);
    }

    /// <summary>请求创建新分支时由视图弹出输入框并调用 GitService.CreateBranchAndCheckout，然后 RefreshStatus。</summary>
    public event EventHandler<string>? CreateBranchRequested;

    /// <summary>请求切换分支时由视图提供分支名并调用 GitService.CheckoutBranch，然后 RefreshStatus。</summary>
    public void CheckoutBranch(string branchName)
    {
        if (_selectedRepository == null) return;
        var (success, error) = GitService.CheckoutBranch(_selectedRepository.WorkingDirectory, branchName);
        if (success)
            RefreshStatus();
        else
            StatusMessage = error ?? "切换失败";
    }

    private async void PullAsync()
    {
        if (_selectedRepository == null) return;
        PullPushError = null;
        var (success, error) = await GitService.PullAsync(_selectedRepository.WorkingDirectory, (url, user, _) => CredentialsProvider?.Invoke(url, user)).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (success)
            {
                RefreshStatus();
                PullPushError = null;
            }
            else
                PullPushError = error;
        });
    }

    private async void PushAsync()
    {
        if (_selectedRepository == null) return;
        PullPushError = null;
        var (success, error) = await GitService.PushAsync(_selectedRepository.WorkingDirectory, (url, user, _) => CredentialsProvider?.Invoke(url, user)).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (success)
            {
                RefreshStatus();
                PullPushError = null;
            }
            else
                PullPushError = error;
        });
    }
}
