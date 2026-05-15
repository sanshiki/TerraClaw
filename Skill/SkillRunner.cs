using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
namespace TerraClaw.Skill;

/// <summary>
/// Base class for built-in C# skills that execute autonomously within the game.
/// Skills run as state machines without per-frame runtime communication.
/// </summary>
public abstract class BuiltinSkill
{
    public string SkillId { get; set; } = Guid.NewGuid().ToString();
    public string SkillName => GetType().Name.Replace("Skill", "").ToSnakeCase();
    public SkillState State { get; protected set; } = SkillState.Idle;
    public float Progress { get; protected set; }
    public string? StatusMessage { get; protected set; }
    public object? ResultData { get; protected set; }

    protected Player Player => Main.LocalPlayer;

    public virtual bool CheckPreconditions() => true;

    public virtual void Start(Dictionary<string, object> parameters) { }
    public virtual SkillTickResult Tick(int elapsedTicks) => SkillTickResult.Running();
    public virtual void Cancel() => State = SkillState.Cancelled;

    protected void Complete(object? result = null)
    {
        State = SkillState.Completed;
        Progress = 1f;
        ResultData = result;
    }

    protected void Fail(string reason)
    {
        State = SkillState.Failed;
        StatusMessage = reason;
    }
}

internal static class SkillExtensions
{
    public static string ToSnakeCase(this string input)
    {
        return string.Concat(input.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}

public enum SkillState
{
    Idle,
    CheckingPreconditions,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public class SkillTickResult
{
    public bool IsComplete { get; init; }

    public static SkillTickResult Running(string? status = null)
        => new() { IsComplete = false };
    public static SkillTickResult Done()
        => new() { IsComplete = true };
}
