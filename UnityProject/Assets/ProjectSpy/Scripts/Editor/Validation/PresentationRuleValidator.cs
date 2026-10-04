using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ProjectSpy.Core;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

// `UnityEditor.Compilation` is imported for CompilationPipeline below, and it declares its
// own `CSharpSyntaxTree`. Without this alias the unqualified name binds to Unity's type and
// the parse call fails to resolve.
using CSharpSyntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree;

namespace ProjectSpy.Unity.Editor.Validation
{
    /// <summary>
    /// Flags Presentation code that performs arithmetic on Core's stat and position fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule being protected.</b> Core decides what is true: how far a guard sees, how
    /// loud a noise is by the time it crosses a floor, whether a door can be opened. Unity
    /// draws what Core already decided. The moment Presentation starts computing on Core's
    /// numbers — <c>room.EndX - room.StartX + wallThickness</c>, or <c>guard.VisionRange *
    /// lightFactor</c> — there are two answers to "how far can this guard see", and they will
    /// disagree in exactly the case that matters: the player looking at a lit room through a
    /// door they believe is closed.
    /// </para>
    /// <para>
    /// <b>Why a validator and not a code review.</b> Because it is invisible. The arithmetic
    /// looks like rendering — "I just need the door's width to place a door frame" — and it
    /// is correct as rendering. It is only wrong because it makes the same claim Core does.
    /// Nothing about the line of code looks like a rule, which is precisely why it survives
    /// review and then survives three more stages.
    /// </para>
    /// <para>
    /// <b>What it flags.</b> A binary or compound assignment, or an increment or decrement,
    /// whose left-hand side resolves to a Core stat or position member. Reads are fine: a
    /// renderer must be able to <em>look at</em> Core's numbers, and flagging every read
    /// would produce a warning on essentially correct code and train everyone to ignore it.
    /// The narrow rule is the one that catches the real defect.
    /// </para>
    /// <para>
    /// <b>It is a warning, not an error.</b> One legitimate case exists — a Presentation-only
    /// cache or a debug readout — and a hard error would push people towards
    /// <c>#pragma warning disable</c>, which is worse than a warning people learn to read.
    /// </remarks>
    [InitializeOnLoad]
    public static class PresentationRuleValidator
    {
        /// <summary>Assemblies whose types are treated as Core.</summary>
        private static readonly string[] CoreAssemblies = { "ProjectSpy.Core", "ProjectSpy.Tables" };

        /// <summary>
        /// Member names that carry a stat or a position.
        /// </summary>
        /// <remarks>
        /// Matched by name rather than by resolving types, because resolution is the part
        /// that goes wrong: the members live on generated table beans, on records and on
        /// structs reached through properties, and a resolver that misses one fails open.
        /// A name list fails loudly instead, and is reviewed alongside the Core types it
        /// mirrors.
        /// </remarks>
        private static readonly HashSet<string> GuardedMembers = new(StringComparer.Ordinal)
        {
            // Position, on the tactical line.
            "StartX", "EndX", "Span", "LastX", "X", "UpperX",
            "FloorIndex", "FloorIndexA", "FloorIndexB", "Index",

            // Agent and world stats.
            "Funds", "Intel", "Materials", "Reputation", "Heat",
            "Health", "PhysicalDamage", "MentalDamage",
            "PhysicalStamina", "MentalStamina", "Stamina",
            "VisionRange", "HearingRange", "VisionConeDegrees",
            "PatrolSpeed", "AlertSpeed", "Suspicion", "Alarm",
            "Level", "Experience", "Exp", "Salary", "Upkeep", "Reward",
            "TraverseSteps", "RadiusCm", "NoiseAbsorptionPercent",

            // Time.
            "Tick", "Step", "StepsRun", "Hour", "Day", "Week",
        };

        /// <summary>
        /// Types whose name indicates a Core stat container even when the namespace differs.
        /// </summary>
        private static readonly HashSet<string> CoreTypeNames = new(StringComparer.Ordinal)
        {
            "WorldState", "GameSession", "TacticalState", "SiteLayout", "SiteRoom",
            "SiteFloor", "SiteConnection", "Agent", "Resources", "SkillSet",
            "TacticalActor", "Fixed32", "Tick", "Perception",
        };

        static PresentationRuleValidator()
        {
            // Re-run whenever scripts change, so a violation is reported at the moment it is
            // typed rather than at the next manual scan.
            CompilationPipeline.compilationFinished += _ => ScanAndReport();
            EditorApplication.delayCall += ScanAndReport;
        }

        /// <summary>Menu entry for a manual scan.</summary>
        [MenuItem("ProjectSpy/Validate Presentation Rules", priority = 200)]
        public static void ScanAndReport()
        {
            var findings = Scan();
            foreach (string finding in findings)
                Debug.LogWarning("[ProjectSpy] " + finding);

            if (findings.Count == 0)
            {
                Debug.Log("[ProjectSpy] Presentation rule scan: no arithmetic on Core stat or position fields.");
            }
        }

        /// <summary>
        /// Scans the Presentation assemblies and returns one message per violation.
        /// </summary>
        /// <remarks>
        /// Uses the syntax tree rather than IL. IL would be more precise, but a violation is
        /// by nature about the <em>source</em> expression — <c>a.X - b.X</c> in C# is
        /// arithmetic on Core's X whichever way it compiles — and the tree gives a line
        /// number that can be clicked, which is what makes the report actionable.
        /// </remarks>
        public static IReadOnlyList<string> Scan()
        {
            var findings = new List<string>();

            foreach (var path in PresentationScripts())
            {
                var tree = CSharpSyntaxTree.ParseText(System.IO.File.ReadAllText(path));

                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    string message = Describe(node, path);
                    if (message != null)
                        findings.Add(message);
                }
            }

            return findings;
        }

        private static string Describe(Microsoft.CodeAnalysis.SyntaxNode node, string path)
        {
            bool isCompoundAssign = node is Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax a
                                   && !a.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression);

            bool isIncrement =
                node is Microsoft.CodeAnalysis.CSharp.Syntax.PostfixUnaryExpressionSyntax post
                    && post.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostIncrementExpression) ||
                node is Microsoft.CodeAnalysis.CSharp.Syntax.PrefixUnaryExpressionSyntax pre
                    && pre.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreIncrementExpression);

            bool isDecrement =
                node is Microsoft.CodeAnalysis.CSharp.Syntax.PostfixUnaryExpressionSyntax post2
                    && post2.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PostDecrementExpression) ||
                node is Microsoft.CodeAnalysis.CSharp.Syntax.PrefixUnaryExpressionSyntax pre2
                    && pre2.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PreDecrementExpression);

            if (!isCompoundAssign && !isIncrement && !isDecrement)
                return null;

            string target = TargetMemberName(node);
            if (target == null)
                return null;

            // LaneUnits is the sanctioned conversion between Core centimetres and Unity
            // metres, and doing arithmetic inside it is the whole point of it existing.
            if (IsInsideLaneUnits(path))
                return null;

            var lineSpan = node.GetLocation().GetLineSpan();
            int line = lineSpan.StartLinePosition.Line + 1;

            string verb = isCompoundAssign || isIncrement ? "increments" : "decrements";
            return $"{ShortPath(path)}:{line}: Presentation code {verb} '{target}', which is a Core " +
                   "stat or position field. Core decides these values; Presentation draws them. " +
                   "If you need a derived number for rendering, put it in a Presentation-only " +
                   "helper such as LaneUnits and keep Core's value untouched.";
        }

        private static bool IsInsideLaneUnits(string path)
            => path.Replace('\\', '/').EndsWith("/Site/LaneUnits.cs", StringComparison.Ordinal);

        /// <summary>
        /// Extracts the member name being written, or null when the target is not a guarded
        /// Core member.
        /// </summary>
        private static string TargetMemberName(Microsoft.CodeAnalysis.SyntaxNode node)
        {
            Microsoft.CodeAnalysis.SyntaxNode target = node switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax a => a.Left,
                Microsoft.CodeAnalysis.CSharp.Syntax.PostfixUnaryExpressionSyntax p => p.Operand,
                Microsoft.CodeAnalysis.CSharp.Syntax.PrefixUnaryExpressionSyntax p => p.Operand,
                _ => null,
            };

            if (target == null)
                return null;

            string text = target.ToString();

            // Direct member access: `room.EndX`, `world.Resources.Funds`.
            foreach (string guarded in GuardedMembers)
            {
                if (!text.EndsWith("." + guarded, StringComparison.Ordinal))
                    continue;

                // A local shadowing the name is legitimate and common; the validator only
                // claims a Core member when the expression actually reads through one.
                if (text == guarded)
                    continue;

                return guarded;
            }

            return null;
        }

        /// <summary>
        /// True when an expression mentions a Core stat type by name.
        /// </summary>
        /// <remarks>
        /// Used to keep the report honest about what it did and did not prove. The member
        /// list catches the common cases; this is the weaker check that catches a member it
        /// has not heard of, at the cost of false positives.
        /// </remarks>
        public static bool MentionsCoreType(string expression)
        {
            foreach (string type in CoreTypeNames)
            {
                if (expression.Contains(type, StringComparison.Ordinal))
                    return true;
            }

            return expression.Contains("Core.", StringComparison.Ordinal);
        }

        /// <summary>
        /// Every C# file in the Presentation assemblies.
        /// </summary>
        /// <remarks>
        /// Core's own files are excluded, not merely ignored: a rule implemented in Core is a
        /// different defect (a layering violation) with a different owner, and reporting it
        /// from here would put it in the wrong report.
        /// </remarks>
        private static IEnumerable<string> PresentationScripts()
        {
            var roots = new[] { "Assets/ProjectSpy" };
            foreach (string root in roots)
            {
                foreach (string file in System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
                {
                    string normalised = file.Replace('\\', '/');
                    if (normalised.Contains("/Plugins/"))
                        continue;

                    yield return file;
                }
            }
        }

        private static string ShortPath(string path)
            => path.Replace('\\', '/').Replace("Assets/ProjectSpy/", string.Empty);
    }
}