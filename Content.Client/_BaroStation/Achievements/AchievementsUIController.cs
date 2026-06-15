using Content.Shared._BaroStation.Achievements;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using System.Linq;
using Robust.Shared.Timing;

namespace Content.Client._BaroStation.Achievements;

public sealed partial class AchievementsUIController : UIController
{
    [Dependency] private IClientNetManager _netManager = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;
    private static AchievementsUIController? _instance;
    private AchievementsWindow? _window;
    public static event Action<HashSet<string>>? OnAchievementsUpdated;
    private List<AchievementPrototype> _allAchievements = new();
    private HashSet<string> _earnedAchievements = new();
    private bool _hasCachedData;
    private NetUserId? _lastUserId;
    private bool _isRequestingData;
    private bool _isConnected;
    private float _timeoutAccumulator;
    private const float TimeoutDuration = 5f;

    public override void Initialize()
    {
        _instance = this;
        base.Initialize();

        _sawmill = _logManager.GetSawmill("achievements.ui");

        foreach (var proto in _prototypeManager.EnumeratePrototypes<AchievementPrototype>())
        {
            _allAchievements.Add(proto);
        }

        _allAchievements.Sort((a, b) => string.Compare(a.ID, b.ID, StringComparison.Ordinal));

        SubscribeNetworkEvent<AchievementEarnedMessage>(OnAchievementEarned);
        SubscribeNetworkEvent<AchievementsStateMessage>(OnAchievementsState);

        _netManager.ClientConnectStateChanged += OnClientConnectStateChanged;
        _netManager.Disconnect += OnDisconnect;

        _playerManager.LocalSessionChanged += OnLocalSessionChanged;
    }

    public static bool HasAchievementStatic(string achievementId)
    {
        return _instance?._earnedAchievements.Contains(achievementId) ?? false;
    }

    private void OnLocalSessionChanged((ICommonSession? Old, ICommonSession? New) args)
    {
        var newUserId = args.New?.UserId;

        if (_lastUserId != newUserId)
        {
            ClearCache();

            if (_isConnected && newUserId != null)
            {
                RequestAchievements();
            }
        }

        _lastUserId = newUserId;
    }

    private void ClearCache()
    {
        _earnedAchievements.Clear();
        _hasCachedData = false;
        _isRequestingData = false;
        _timeoutAccumulator = 0;

        if (_window != null && !_window.Disposed)
        {
            _window.ClearAchievements();
            if (_window.IsOpen)
            {
                _window.ShowLoadingState();
            }
        }
    }

    private void OnClientConnectStateChanged(ClientConnectionState state)
    {
        if (state == ClientConnectionState.Connected)
        {
            _sawmill.Debug("Client connected to server, requesting achievements...");
            _isConnected = true;
            ClearCache();
            RequestAchievements();
        }
        else if (state == ClientConnectionState.NotConnecting)
        {
            _sawmill.Debug("Client disconnected from server");
            _isConnected = false;
            ClearCache();
        }
    }

    private void OnDisconnect(object? sender, NetDisconnectedArgs e)
    {
        _sawmill.Debug($"Client disconnected: {e.Reason}");
        _isConnected = false;
        ClearCache();
    }

    private void EnsureWindow()
    {
        if (_window != null && !_window.Disposed)
            return;

        _window = UIManager.CreateWindow<AchievementsWindow>();

        _window.OnOpen += OnWindowOpened;

        if (_hasCachedData)
        {
            _window.CacheAchievements(_allAchievements, _earnedAchievements);
        }
        else if (_isConnected)
        {
            _window.ShowLoadingState();
            RequestAchievements();
        }
    }

    private void OnWindowOpened()
    {
        if (!_hasCachedData && !_isRequestingData && _isConnected)
        {
            RequestAchievements();
        }
        else if (_hasCachedData && _window != null && !_window.Disposed)
        {
            _window.UpdateAchievements(_allAchievements, _earnedAchievements);
        }
        else if (!_isConnected && _window != null && !_window.Disposed)
        {
            _window.ShowNotConnectedState();
        }
    }

    public void ToggleWindow()
    {
        EnsureWindow();

        if (_window == null || _window.Disposed)
            return;

        if (_window.IsOpen)
        {
            _window.Close();
        }
        else
        {
            _window.OpenCentered();
        }
    }

    public void RequestAchievements()
    {
        if (!_isConnected)
        {
            _sawmill.Debug("Cannot request achievements: not connected to server");
            return;
        }

        if (_isRequestingData)
        {
            _sawmill.Debug("Already requesting achievements, skipping");
            return;
        }

        _sawmill.Debug("Requesting achievements from server...");
        _isRequestingData = true;
        _timeoutAccumulator = 0;

        if (_window != null && !_window.Disposed && _window.IsOpen)
        {
            _window.ShowLoadingState();
        }

        var msg = new RequestAchievementsMessage();
        _entityManager.EventBus.RaiseEvent(EventSource.Network, msg);
    }

    public void Update(float frameTime)
    {
        if (_isRequestingData)
        {
            _timeoutAccumulator += frameTime;
            if (_timeoutAccumulator >= TimeoutDuration)
            {
                _sawmill.Warning($"Request achievements timeout after {TimeoutDuration} seconds");
                _isRequestingData = false;
                if (_window != null && !_window.Disposed && _window.IsOpen && !_hasCachedData)
                {
                    _window.ShowErrorState();
                }
            }
        }
    }

    private void OnAchievementsState(AchievementsStateMessage msg, EntitySessionEventArgs args)
    {
        _sawmill.Debug($"Received achievements state with {msg.EarnedIds.Count} earned achievements");

        _earnedAchievements = new HashSet<string>(msg.EarnedIds);
        _hasCachedData = true;
        _isRequestingData = false;
        _timeoutAccumulator = 0;

        if (_window != null && !_window.Disposed)
        {
            _window.CacheAchievements(_allAchievements, _earnedAchievements);

            if (_window.IsOpen)
            {
                _window.UpdateAchievements(_allAchievements, _earnedAchievements);
            }
        }

        OnAchievementsUpdated?.Invoke(_earnedAchievements);
    }

    public bool HasAchievement(string achievementId)
    {
        return _earnedAchievements.Contains(achievementId);
    }

    private void OnAchievementEarned(AchievementEarnedMessage msg, EntitySessionEventArgs args)
    {
        if (!_allAchievements.Any(a => a.ID == msg.AchievementId))
        {
            return;
        }

        _sawmill.Debug($"Achievement earned: {msg.AchievementId}");

        _earnedAchievements.Add(msg.AchievementId);
        _hasCachedData = true;

        if (_window != null && !_window.Disposed)
        {
            _window.CacheAchievements(_allAchievements, _earnedAchievements);

            if (_window.IsOpen)
            {
                _window.UpdateAchievements(_allAchievements, _earnedAchievements);
            }
        }

        var proto = _allAchievements.FirstOrDefault(a => a.ID == msg.AchievementId);
        if (proto != null)
        {
            ShowAchievementToast(proto);
        }
    }

    private void ShowAchievementToast(AchievementPrototype proto)
    {
        var toast = new ToastNotification(proto);
        toast.OnClosed += () => toast.Dispose();
        toast.Show();
    }
}
