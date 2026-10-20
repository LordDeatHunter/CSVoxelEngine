using System.Numerics;

namespace VoxelEngine.Player.Posing;

/// <summary>Where each shoulder is pulled toward its reaching arm (jump, swim stroke, cocked throw, slice sweep, punch), the target the owner's pull springs chase.</summary>
internal sealed class ShoulderReach
{
    private readonly PlayerModel _model;

    internal ShoulderReach(PlayerModel model) => _model = model;

    internal const float MaxShoulderPull = 0.08f;

    // ---- Shoulder pull -------------------------------------------------------------------
    // The shoulders ride on the torso (they are children of its frame), so they can never detach from it.
    // On top of that a heavily extended arm drags its shoulder a little toward where it reaches, instead of
    // the limb stretching away from a body that stays put: torso-space offsets (x right, y up, z back)
    // driven by how far each action has taken the arm out, never more than MaxShoulderPull.
    internal Vector3 PullTarget(bool right)
    {
        var side = right ? 1f : -1f;
        var pull = Vector3.Zero;

        // Arms thrown out in a jump lift and widen their shoulders.
        var air = _model.AirBlend * (1f - _model.SwimBlend);
        pull += new Vector3(0.015f * side, 0.025f, 0f) * air;
        // The swim stroke throws both shoulders out with the arms.
        pull += new Vector3(0.025f * side, 0.015f, -0.02f) * (_model.SwimBlend * _model.Swim.StrokeOut());

        if (right)
        {
            // A cocked throw reaches out to the right and back and lifts the shoulder.
            pull += new Vector3(0.035f, 0.025f, 0.035f) * _model.ThrowArm.ThrowArmWeight();
            // A slice drags the shoulder across with the sweep.
            pull += new Vector3(-0.035f * SliceMotion.SliceBodySweep(_model.SliceProgress, _model.SliceDir), 0f, 0f);
            if (_model.MineT < 1f)
            {
                var w = PunchPose.PunchWeights(_model.MineT);
                // The chambered elbow flares out, the thrust pushes the shoulder forward after the fist.
                pull += new Vector3(0.03f, 0f, 0f) * (w.Retract * (1f - w.Extend) * (1f - w.Recover));
                pull += new Vector3(0f, 0.01f, -0.05f) * (w.Extend * (1f - w.Recover));
            }
        }
        else if (_model.MineT < 1f)
        {
            // The off hand swings back as the fist drives out and takes its shoulder with it.
            pull += new Vector3(0f, 0f, 0.03f) * PunchPose.PunchWeights(_model.MineT).Extend;
        }

        var length = pull.Length();
        return length > MaxShoulderPull ? pull * (MaxShoulderPull / length) : pull;
    }
}
