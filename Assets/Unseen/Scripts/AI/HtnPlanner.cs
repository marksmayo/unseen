using System;
using System.Collections.Generic;

namespace Unseen.AI
{
    /// <summary>Base of the task hierarchy.</summary>
    public abstract class HtnTask
    {
        public string Name { get; protected set; } = "task";

        /// <summary>Gate on the current world state. A task whose precondition fails is skipped.</summary>
        public Func<BotFacts, bool> Precondition;

        public bool IsValid(in BotFacts facts) => Precondition == null || Precondition(facts);
    }

    /// <summary>A leaf task: one concrete action the bot can execute.</summary>
    public sealed class PrimitiveTask : HtnTask
    {
        public BotAction Action;

        /// <summary>How long the bot commits to this action before it replans.</summary>
        public float Duration;

        public PrimitiveTask(string name, BotAction action, Func<BotFacts, bool> precondition = null, float duration = 0.5f)
        {
            Name = name;
            Action = action;
            Precondition = precondition;
            Duration = duration;
        }
    }

    /// <summary>One way of accomplishing a compound task.</summary>
    public sealed class HtnMethod
    {
        public readonly string Name;
        public readonly Func<BotFacts, bool> Condition;
        public readonly List<HtnTask> Subtasks = new List<HtnTask>();

        public HtnMethod(string name, Func<BotFacts, bool> condition, params HtnTask[] subtasks)
        {
            Name = name;
            Condition = condition;
            Subtasks.AddRange(subtasks);
        }

        public bool IsValid(in BotFacts facts) => Condition == null || Condition(facts);
    }

    /// <summary>
    /// A compound task, decomposed by the first method whose condition holds. Method order is
    /// priority order, which keeps the domain readable: the most urgent branch is written first.
    /// </summary>
    public sealed class CompoundTask : HtnTask
    {
        public readonly List<HtnMethod> Methods = new List<HtnMethod>();

        public CompoundTask(string name)
        {
            Name = name;
        }

        public CompoundTask With(HtnMethod method)
        {
            Methods.Add(method);
            return this;
        }
    }

    /// <summary>
    /// Hierarchical Task Network planner. Depth-first decomposition over ordered methods, rolling
    /// back to the next method whenever a branch turns out not to apply. It reasons over a plain
    /// struct of facts and allocates nothing per plan, which is what makes replanning 63 bots
    /// inside one server tick affordable.
    /// </summary>
    public sealed class HtnPlanner
    {
        public int MethodsTried { get; private set; }

        /// <summary>
        /// Fills <paramref name="plan"/> with an ordered list of primitives, or leaves it empty when
        /// nothing in the domain applies to these facts.
        /// </summary>
        public bool Plan(CompoundTask root, in BotFacts facts, List<PrimitiveTask> plan, int maxDepth = 8)
        {
            plan.Clear();
            MethodsTried = 0;
            return root != null && Decompose(root, facts, plan, maxDepth);
        }

        private bool Decompose(CompoundTask task, in BotFacts facts, List<PrimitiveTask> plan, int depth)
        {
            if (depth <= 0 || !task.IsValid(facts)) return false;

            for (int m = 0; m < task.Methods.Count; m++)
            {
                HtnMethod method = task.Methods[m];
                if (!method.IsValid(facts)) continue;

                MethodsTried++;
                int mark = plan.Count;
                bool complete = true;

                for (int s = 0; s < method.Subtasks.Count; s++)
                {
                    HtnTask subtask = method.Subtasks[s];

                    if (subtask is PrimitiveTask primitive)
                    {
                        if (!primitive.IsValid(facts))
                        {
                            complete = false;
                            break;
                        }

                        plan.Add(primitive);
                        continue;
                    }

                    if (subtask is CompoundTask compound)
                    {
                        if (Decompose(compound, facts, plan, depth - 1)) continue;
                        complete = false;
                        break;
                    }
                }

                if (complete) return true;

                // Roll the plan back to the state before this method and try the next one.
                if (plan.Count > mark) plan.RemoveRange(mark, plan.Count - mark);
            }

            return false;
        }
    }
}
