using System.Collections.Generic;

namespace MozaPlugin.Devices.StalksTruckSim
{
    /// <summary>
    /// Pure planning for stepping a game's cycling control (wipers, light knob) from a
    /// current stage to a target stage using forward / back cycle-key taps.
    ///
    /// Two control shapes:
    ///   • Wipers  — a forward key (<c>P</c>) and a back key (<c>-</c>). If the control
    ///     wraps, the shortest of the two directions is chosen (ties prefer forward).
    ///   • Lights  — a single forward-only cycle key (<c>L</c>) that wraps; steps always
    ///     go forward around the ring.
    ///
    /// Deterministic and side-effect free so it can be unit-tested in isolation.
    /// </summary>
    public static class StageCycle
    {
        /// <summary>
        /// Plan the key taps to move from <paramref name="current"/> to
        /// <paramref name="target"/>. Each element is <c>+1</c> for a forward tap and
        /// <c>-1</c> for a back tap. Empty when already at the target.
        /// </summary>
        /// <param name="current">Current stage (clamped/wrapped into [0, stageCount)).</param>
        /// <param name="target">Target stage (clamped/wrapped into [0, stageCount)).</param>
        /// <param name="stageCount">Number of stages (&gt;= 1).</param>
        /// <param name="wrap">Whether the forward key wraps past the last stage to 0.</param>
        /// <param name="hasBackKey">Whether a distinct back key exists (wipers: true, lights: false).</param>
        /// <remarks>
        /// Worked examples (stageCount = 4):
        ///   PlanSteps(0, 2, 4, wrap:true,  back:true)  → [+1,+1]
        ///   PlanSteps(3, 2, 4, wrap:true,  back:true)  → [-1]           (back ×1 beats fwd-wrap ×3)
        ///   PlanSteps(3, 0, 4, wrap:true,  back:true)  → [+1]           (fwd-wrap ×1 beats back ×3)
        ///   PlanSteps(3, 1, 4, wrap:true,  back:true)  → [+1,+1]        (tie → prefer forward)
        ///   PlanSteps(3, 1, 4, wrap:false, back:true)  → [-1,-1]        (linear, no wrap)
        ///   PlanSteps(2, 0, 3, wrap:true,  back:false) → [+1]           (lights: forward-only wrap)
        ///   PlanSteps(1, 1, 4, ...)                    → []             (no-op)
        /// </remarks>
        public static IReadOnlyList<int> PlanSteps(int current, int target, int stageCount, bool wrap, bool hasBackKey)
        {
            var steps = new List<int>();
            if (stageCount <= 1) return steps;

            current = Mod(current, stageCount);
            target = Mod(target, stageCount);
            if (current == target) return steps;

            int forwardDist = Mod(target - current, stageCount);   // taps going forward (with wrap)
            int backwardDist = Mod(current - target, stageCount);  // taps going backward (with wrap)

            if (!hasBackKey)
            {
                // Forward-only cycle key (e.g. lights' L). Must wrap forward around the ring.
                for (int i = 0; i < forwardDist; i++) steps.Add(+1);
                return steps;
            }

            int fwd, back;
            if (wrap)
            {
                fwd = forwardDist;
                back = backwardDist;
            }
            else
            {
                // No wrap: only the linear direction is reachable.
                if (target > current) { fwd = target - current; back = int.MaxValue; }
                else { fwd = int.MaxValue; back = current - target; }
            }

            if (fwd <= back)
                for (int i = 0; i < fwd; i++) steps.Add(+1);
            else
                for (int i = 0; i < back; i++) steps.Add(-1);

            return steps;
        }

        private static int Mod(int a, int m)
        {
            int r = a % m;
            return r < 0 ? r + m : r;
        }
    }
}
