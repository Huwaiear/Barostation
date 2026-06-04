using Robust.Shared.Prototypes;

namespace Content.Shared._BaroStation.Achievements;

/// <summary>
/// Компонент для прототипа достижения, определяющий условия его получения
/// </summary>
[Prototype] // Убрано явное указание "achievementCondition"
public sealed partial class AchievementConditionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField("conditionType", required: true)]
    public AchievementConditionType Type { get; private set; }

    [DataField("targetEntityIds")]
    public List<string> TargetEntityIds { get; private set; } = new();

    [DataField("targetEntityContains")]
    public string? TargetEntityContains { get; private set; }

    [DataField("targetComponents")]
    public List<string> TargetComponents { get; private set; } = new();
}

public enum AchievementConditionType
{
    EquipItem,
    KillMob,
}
