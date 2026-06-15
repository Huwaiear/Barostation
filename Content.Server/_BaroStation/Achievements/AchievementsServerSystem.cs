using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._BaroStation.Achievements;
using Content.Shared.Clothing.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Inventory.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server._BaroStation.Achievements;

public sealed partial class AchievementsServerSystem : SharedAchievementsSystem
{
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IServerDbManager _dbManager = default!;
    [Dependency] private ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;
    private Dictionary<NetUserId, HashSet<string>> _playerAchievements = new();
    public static AchievementsServerSystem? Instance { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        Instance = this;

        _sawmill = _logManager.GetSawmill("achievements.server");

        SubscribeNetworkEvent<RequestAchievementsMessage>(OnRequestAchievements);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<GotEquippedEvent>(OnGotEquipped);
        SubscribeLocalEvent<DamageableComponent, DamageChangedEvent>(OnDamageChanged);

        // Подписываемся на подключение игрока
        SubscribeNetworkEvent<PlayerConnectMsg>(OnPlayerConnect);

        // Отправляем достижения при входе в лобби
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerJoinedLobby);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        Instance = null;
        _playerAchievements.Clear();
    }

    private async void OnPlayerConnect(PlayerConnectMsg msg, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (session == null)
            return;

        await LoadAchievementsAsync(session.UserId);
        await SendAchievementsToClient(session);
    }

    private async void OnPlayerJoinedLobby(PlayerJoinedLobbyEvent ev)
    {
        await SendAchievementsToClient(ev.PlayerSession);
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        await LoadAchievementsAsync(ev.Player.UserId);
        await SendAchievementsToClient(ev.Player);

        var achievementComp = EnsureComp<PlayerAchievementsComponent>(ev.Mob);
        if (_playerAchievements.TryGetValue(ev.Player.UserId, out var earned))
            achievementComp.EarnedAchievements = earned;
    }

    private async Task LoadAchievementsAsync(NetUserId userId)
    {
        try
        {
            var earnedIds = await _dbManager.GetPlayerAchievementsAsync(userId);
            _playerAchievements[userId] = new HashSet<string>(earnedIds);
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to load achievements for {userId}: {ex.Message}");
        }
    }

    public override bool HasAchievement(NetUserId userId, string achievementId)
    {
        return _playerAchievements.TryGetValue(userId, out var achievements) &&
               achievements.Contains(achievementId);
    }

    private async void OnGotEquipped(GotEquippedEvent args)
    {
        var wearer = args.EquipTarget;
        if (!TryComp<PlayerAchievementsComponent>(wearer, out var achievementComp))
            return;

        var prototypeId = MetaData(args.Equipment).EntityPrototype?.ID;
        if (string.IsNullOrEmpty(prototypeId))
            return;

        if (TryComp<MaskComponent>(args.Equipment, out var mask) && mask.IsToggled)
            return;

        foreach (var achievement in _prototypeManager.EnumeratePrototypes<AchievementPrototype>())
        {
            if (achievementComp.EarnedAchievements.Contains(achievement.ID))
                continue;

            if (achievement.Condition is EquipItemCondition equipCondition)
            {
                foreach (var requiredItem in equipCondition.Items)
                {
                    if (prototypeId == requiredItem.Id)
                    {
                        await GrantAchievement(wearer, achievementComp, achievement.ID);
                        break;
                    }
                }
            }
        }
    }

    private async void OnDamageChanged(EntityUid uid, DamageableComponent component, DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        if (!TryComp<MobStateComponent>(uid, out var mobState) ||
            mobState.CurrentState != MobState.Dead)
            return;

        var killer = args.Origin;
        if (killer == null || killer == uid)
            return;

        if (!TryComp<PlayerAchievementsComponent>(killer, out var killerComp))
            return;

        var mobPrototypeId = MetaData(uid).EntityPrototype?.ID ?? string.Empty;

        foreach (var achievement in _prototypeManager.EnumeratePrototypes<AchievementPrototype>())
        {
            if (killerComp.EarnedAchievements.Contains(achievement.ID))
                continue;

            if (achievement.Condition is KillMobCondition killCondition)
            {
                bool matches = false;

                if (!string.IsNullOrEmpty(killCondition.ContainsId))
                    matches = mobPrototypeId.Contains(killCondition.ContainsId);

                if (!matches && killCondition.ExactIds.Count > 0)
                    matches = killCondition.ExactIds.Contains(mobPrototypeId);

                if (matches)
                {
                    await GrantAchievement(killer.Value, killerComp, achievement.ID);
                }
            }
        }
    }

    private async Task GrantAchievement(EntityUid player, PlayerAchievementsComponent component, string achievementId)
    {
        if (component.EarnedAchievements.Contains(achievementId))
            return;

        if (!_prototypeManager.HasIndex<AchievementPrototype>(achievementId))
            return;

        component.EarnedAchievements.Add(achievementId);
        Dirty(player, component);

        if (TryComp<ActorComponent>(player, out var actor))
        {
            var userId = actor.PlayerSession.UserId;
            await _dbManager.AddPlayerAchievementAsync(userId, achievementId);

            if (!_playerAchievements.ContainsKey(userId))
                _playerAchievements[userId] = new HashSet<string>();
            _playerAchievements[userId].Add(achievementId);

            RaiseNetworkEvent(new AchievementEarnedMessage { AchievementId = achievementId }, actor.PlayerSession);
            RaiseNetworkEvent(new AchievementsStateMessage { EarnedIds = component.EarnedAchievements.ToList() }, actor.PlayerSession);
        }
    }

    private async void OnRequestAchievements(RequestAchievementsMessage msg, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (session == null)
            return;

        await LoadAchievementsAsync(session.UserId);
        await SendAchievementsToClient(session);
    }

    private async Task SendAchievementsToClient(ICommonSession session)
    {
        try
        {
            if (!_playerAchievements.ContainsKey(session.UserId))
            {
                await LoadAchievementsAsync(session.UserId);
            }

            var earnedIds = _playerAchievements.GetValueOrDefault(session.UserId, new HashSet<string>());
            var message = new AchievementsStateMessage { EarnedIds = earnedIds.ToList() };

            _sawmill.Info($"Sending achievements to {session.Name}: {earnedIds.Count} earned");

            RaiseNetworkEvent(message, session);
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to send achievements to {session.Name}: {ex.Message}");
            RaiseNetworkEvent(new AchievementsStateMessage { EarnedIds = new List<string>() }, session);
        }
    }
}
