using System;
using System.Collections.Generic;
using VoxelEngine.Content.Quests;

namespace VoxelEngine.Quests;

/// <summary>
/// The quest-complete toasts waiting to be shown, presented strictly one card at a time: each slides in
/// from the right (<see cref="QuestConfig.ToastSlideInSeconds"/>, fading in over its start), holds
/// (<see cref="QuestConfig.ToastHoldSeconds"/>), then slides and fades back out
/// (<see cref="QuestConfig.ToastSlideOutSeconds"/>), and the next queued card follows. GL-free; main thread only.
/// </summary>
public sealed class QuestToastQueue
{
    private const float TotalSeconds =
        QuestConfig.ToastSlideInSeconds + QuestConfig.ToastHoldSeconds + QuestConfig.ToastSlideOutSeconds;

    private readonly Queue<QuestType> _waiting = new();
    private float _age;

    /// <summary>The card on screen, or null when the queue has run dry.</summary>
    public QuestType? Current { get; private set; }

    /// <summary>How far the card sits off its resting spot, 0 (resting) to 1 (fully off the right edge).</summary>
    public float Slide
    {
        get
        {
            if (Current == null) return 1f;
            if (_age < QuestConfig.ToastSlideInSeconds)
                return 1f - MathUtils.EaseOutCubic(_age / QuestConfig.ToastSlideInSeconds);
            var outAge = _age - QuestConfig.ToastSlideInSeconds - QuestConfig.ToastHoldSeconds;
            return outAge <= 0f ? 0f : MathUtils.EaseInCubic(MathUtils.Clamp01(outAge / QuestConfig.ToastSlideOutSeconds));
        }
    }

    /// <summary>The card's opacity, 0..1.</summary>
    public float Alpha
    {
        get
        {
            if (Current == null) return 0f;
            if (_age < QuestConfig.ToastFadeInSeconds) return _age / QuestConfig.ToastFadeInSeconds;
            var outAge = _age - QuestConfig.ToastSlideInSeconds - QuestConfig.ToastHoldSeconds;
            return outAge <= 0f ? 1f : 1f - MathUtils.Clamp01(outAge / QuestConfig.ToastSlideOutSeconds);
        }
    }

    public void Push(QuestType quest) => _waiting.Enqueue(quest);

    /// <summary>Advances the card on screen by <paramref name="deltaSeconds"/>; returns the quest whose card
    /// started this step (the caller plays the sound once per card), or null.</summary>
    public QuestType? Step(float deltaSeconds)
    {
        if (Current != null)
        {
            _age += Math.Max(0f, deltaSeconds);
            if (_age < TotalSeconds) return null;
            Current = null;
        }
        if (!_waiting.TryDequeue(out var next)) return null;
        Current = next;
        _age = 0f;
        return next;
    }
}
