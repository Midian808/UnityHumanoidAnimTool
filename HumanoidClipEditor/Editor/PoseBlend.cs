using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// C16c–f, C16h: the pose keys of a clip blended into one layer, evaluated per frame. Each body part has its own
    /// track made of the unmuted keys whose mask covers it, so a key that poses only the arms leaves the other parts
    /// to the clip underneath or to other keys.
    ///
    /// <b>Blending.</b> Between two keys of a track each value follows a curve through the keys, shaped by the
    /// earlier key's <see cref="PoseEasing"/> (Smooth: a monotone cubic, flat at the first and last key, never past a key; Linear; Hold: a step).
    /// The hips' place is blended as height, horizontal position, facing (an angle about the vertical, unwrapped, so a
    /// turn goes the short way between keys) and tilt (a rotation kept on one hemisphere, so it never flips).
    ///
    /// <b>Where keys reach.</b> On a new clip the keys of a track always blend into each other, and the first and
    /// last keys hold to the clip's ends. On a fixed clip keys form groups: two neighbouring keys blend into each
    /// other when their ease windows meet (the gap is at most the first's ease out plus the second's ease in plus
    /// one frame); outside a group the clip underneath eases into the group's first key over its ease in and back out
    /// of its last over its ease out (smoothstep, like the other records).
    ///
    /// <b>Loops.</b> On a clip with Loop Time on, the last frame is frame 0 again: keys are repeated one loop before
    /// and after, and the last frame takes frame 0's values. The hips' horizontal position and facing are the
    /// exception: they are the clip's travel (C15), which carries on from one loop to the next instead of returning,
    /// so they are not repeated, and a key on the last frame sets only them (where the hips have got to by the end).
    /// </summary>
    public sealed class PoseLayer
    {
        /// <summary>What the layer asks of one frame. A weight of 0 leaves that value to the clip underneath.</summary>
        public sealed class Sample
        {
            public readonly float[] MuscleWeights = new float[HumanTrait.MuscleCount];
            public readonly float[] Muscles = new float[HumanTrait.MuscleCount];
            /// <summary>Weight of the hips' height and tilt.</summary>
            public float BodyWeight;
            /// <summary>Weight of the hips' horizontal position and facing.</summary>
            public float TravelWeight;
            public float HipsHeight;
            public Quaternion HipsTilt = Quaternion.identity;
            public Vector2 HipsHorizontal;
            /// <summary>Degrees about the root's up axis.</summary>
            public float HipsYaw;

            public bool Any => BodyWeight > 0f || TravelWeight > 0f || MuscleWeights.Any(w => w > 0f);
        }

        readonly Track[] muscleTracks = new Track[PartCount];
        readonly int[][] musclesOfPart = new int[PartCount][];
        readonly Track hipsTrack, travelTrack;
        readonly int lastFrame;
        readonly bool loop;

        const int PartCount = 8;

        public bool IsEmpty => muscleTracks.All(t => t == null) && hipsTrack == null && travelTrack == null;

        /// <summary>
        /// The layer made of the unmuted pose keys in <paramref name="records"/>, on a clip whose last frame is
        /// <paramref name="lastFrame"/> and which loops when <paramref name="loop"/>; <paramref name="newClip"/> when
        /// the clip is a new one (keys always blend into each other and hold to the ends).
        /// </summary>
        public PoseLayer(IEnumerable<EditRecord> records, int lastFrame, bool loop, bool newClip)
        {
            this.lastFrame = Mathf.Max(0, lastFrame);
            this.loop = loop && this.lastFrame > 0;
            int period = this.lastFrame;

            // One key per frame: a later key in the list replaces an earlier one on the same frame.
            var byFrame = new Dictionary<int, PoseKeyRecord>();
            foreach (EditRecord r in records)
                if (r is PoseKeyRecord k && !r.muted && k.pose != null && k.pose.IsValid && k.frame >= 0 && k.frame <= this.lastFrame)
                    byFrame[k.frame] = k;
            List<PoseKeyRecord> keys = byFrame.Values.OrderBy(k => k.frame).ToList();

            for (int p = 0; p < PartCount; p++)
            {
                var part = (HumanBodyPart)p;
                musclesOfPart[p] = Enumerable.Range(0, HumanTrait.MuscleCount).Where(m => BodyParts.OfMuscle(m) == part).ToArray();
                List<PoseKeyRecord> covering = keys.Where(k => k.Covers(part)).ToList();
                if (covering.Count == 0 || musclesOfPart[p].Length == 0) continue;
                int[] channels = musclesOfPart[p];
                muscleTracks[p] = Track.Build(Wrapped(covering, period), newClip, k => channels.Select(m => k.pose.muscles[m]).ToArray());
            }

            List<PoseKeyRecord> body = keys.Where(k => k.Covers(HumanBodyPart.Body)).ToList();
            if (body.Count > 0)
            {
                hipsTrack = Track.Build(Wrapped(body, period), newClip, HipsValues, alignQuaternionFrom: 1);
                float lastYaw = float.NaN;
                travelTrack = Track.Build(body.Select(k => (k, k.frame)).ToList(), newClip, k =>
                {
                    Split(k.pose.hipsRotation, out float yaw, out _);
                    if (!float.IsNaN(lastYaw)) yaw = lastYaw + Mathf.DeltaAngle(lastYaw, yaw);
                    lastYaw = yaw;
                    return new[] { k.pose.hipsPosition.x, k.pose.hipsPosition.z, yaw };
                });
            }
        }

        /// <summary>The layer for <paramref name="edits"/> played with <paramref name="timingClip"/>'s length and Loop Time.</summary>
        public static PoseLayer For(ClipEditsAsset edits, IEnumerable<EditRecord> records, AnimationClip timingClip) =>
            new PoseLayer(records, ClipWriter.LastFrame(timingClip), RecordApplier.Loops(timingClip), edits.IsNewClip);

        /// <summary>What the layer asks of <paramref name="frame"/>.</summary>
        public Sample Evaluate(int frame)
        {
            var s = new Sample();
            int wrapped = loop && frame >= lastFrame ? 0 : frame;
            for (int p = 0; p < PartCount; p++)
            {
                Track t = muscleTracks[p];
                if (t == null) continue;
                float[] values = t.Evaluate(wrapped, out float w);
                if (w <= 0f) continue;
                int[] channels = musclesOfPart[p];
                for (int i = 0; i < channels.Length; i++)
                {
                    s.MuscleWeights[channels[i]] = w;
                    s.Muscles[channels[i]] = values[i];
                }
            }
            if (hipsTrack != null)
            {
                float[] v = hipsTrack.Evaluate(wrapped, out s.BodyWeight);
                if (s.BodyWeight > 0f)
                {
                    s.HipsHeight = v[0];
                    s.HipsTilt = EditRecord.Normalized(new Quaternion(v[1], v[2], v[3], v[4]));
                }
            }
            if (travelTrack != null)
            {
                float[] v = travelTrack.Evaluate(frame, out s.TravelWeight);
                if (s.TravelWeight > 0f)
                {
                    s.HipsHorizontal = new Vector2(v[0], v[1]);
                    s.HipsYaw = v[2];
                }
            }
            return s;
        }

        /// <summary>Adds every frame the layer changes (some weight above 0) to <paramref name="frames"/>.</summary>
        public void AddTouchedFrames(ISet<int> frames)
        {
            for (int f = 0; f <= lastFrame; f++)
            {
                int wrapped = loop && f >= lastFrame ? 0 : f;
                bool touched = muscleTracks.Any(t => t != null && t.Weight(wrapped) > 0f)
                               || hipsTrack != null && hipsTrack.Weight(wrapped) > 0f
                               || travelTrack != null && travelTrack.Weight(f) > 0f;
                if (touched) frames.Add(f);
            }
        }

        /// <summary>A rotation split into a turn about the vertical (degrees) and the tilt left after it: <c>q = yaw * tilt</c>.</summary>
        public static void Split(Quaternion q, out float yawDegrees, out Quaternion tilt)
        {
            q = EditRecord.Normalized(q);
            Vector3 forward = q * Vector3.forward;
            if (new Vector2(forward.x, forward.z).sqrMagnitude < 1e-8f) forward = q * Vector3.up; // facing straight up or down
            yawDegrees = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            tilt = EditRecord.Normalized(Quaternion.Inverse(Quaternion.AngleAxis(yawDegrees, Vector3.up)) * q);
        }

        /// <summary>The inverse of <see cref="Split"/>.</summary>
        public static Quaternion Join(float yawDegrees, Quaternion tilt) =>
            EditRecord.Normalized(Quaternion.AngleAxis(yawDegrees, Vector3.up) * EditRecord.Normalized(tilt));

        static float[] HipsValues(PoseKeyRecord k)
        {
            Split(k.pose.hipsRotation, out _, out Quaternion tilt);
            return new[] { k.pose.hipsPosition.y, tilt.x, tilt.y, tilt.z, tilt.w };
        }

        /// <summary>
        /// The keys on the timeline the looping values use: on a loop, repeated one loop before and after, and a key on
        /// the last frame left out (the last frame is frame 0 again); otherwise the keys as they are.
        /// </summary>
        List<(PoseKeyRecord key, int frame)> Wrapped(List<PoseKeyRecord> keys, int period)
        {
            if (!loop) return keys.Select(k => (k, k.frame)).ToList();
            var result = new List<(PoseKeyRecord, int)>();
            foreach (PoseKeyRecord k in keys)
            {
                if (k.frame >= period) continue;
                result.Add((k, k.frame - period));
                result.Add((k, k.frame));
                result.Add((k, k.frame + period));
            }
            return result.OrderBy(x => x.Item2).ToList();
        }

        /// <summary>
        /// One body part's (or the hips') keys on a timeline: groups of keys that blend into each other, curves per
        /// group and value, and the eases around each group.
        /// </summary>
        sealed class Track
        {
            sealed class Key
            {
                public int Frame;
                public float[] Values;
                public PoseEasing Easing;
                public int EaseIn, EaseOut;
            }

            sealed class Group
            {
                public readonly List<Key> Keys = new List<Key>();
                public AnimationCurve[] Curves;
                public int First => Keys[0].Frame;
                public int Last => Keys[Keys.Count - 1].Frame;
            }

            readonly List<Group> groups = new List<Group>();
            bool holdEnds;

            public static Track Build(List<(PoseKeyRecord key, int frame)> keys, bool newClip, Func<PoseKeyRecord, float[]> values,
                                      int alignQuaternionFrom = -1)
            {
                if (keys.Count == 0) return null;
                var t = new Track { holdEnds = newClip };
                Key previous = null;
                Group group = null;
                foreach ((PoseKeyRecord record, int frame) in keys)
                {
                    var k = new Key
                    {
                        Frame = frame, Values = values(record), Easing = record.easing,
                        EaseIn = Mathf.Max(0, record.easeIn), EaseOut = Mathf.Max(0, record.easeOut),
                    };
                    if (alignQuaternionFrom >= 0 && previous != null)
                    {
                        float dot = 0f;
                        for (int c = 0; c < 4; c++) dot += k.Values[alignQuaternionFrom + c] * previous.Values[alignQuaternionFrom + c];
                        if (dot < 0f)
                            for (int c = 0; c < 4; c++) k.Values[alignQuaternionFrom + c] = -k.Values[alignQuaternionFrom + c];
                    }
                    bool bridged = previous != null &&
                                   (newClip || k.Frame - previous.Frame <= previous.EaseOut + k.EaseIn + 1);
                    if (group == null || !bridged)
                    {
                        group = new Group();
                        t.groups.Add(group);
                    }
                    group.Keys.Add(k);
                    previous = k;
                }
                return t;
            }

            /// <summary>The track's weight on <paramref name="frame"/>.</summary>
            public float Weight(int frame)
            {
                Locate(frame, out Group inside, out Key nearest, out float weight);
                return inside != null ? 1f : nearest != null ? weight : 0f;
            }

            /// <summary>The track's values on <paramref name="frame"/> and their weight (0: the track leaves this frame alone).</summary>
            public float[] Evaluate(int frame, out float weight)
            {
                Locate(frame, out Group inside, out Key nearest, out weight);
                if (inside != null)
                {
                    weight = 1f;
                    if (inside.Keys.Count == 1) return inside.Keys[0].Values;
                    AnimationCurve[] curves = inside.Curves ??= BuildCurves(inside);
                    return curves.Select(c => c.Evaluate(frame)).ToArray();
                }
                if (nearest == null) { weight = 0f; return null; }
                return nearest.Values;
            }

            void Locate(int frame, out Group inside, out Key nearest, out float weight)
            {
                inside = null;
                nearest = null;
                weight = 0f;
                if (groups.Count == 0) return;
                foreach (Group g in groups)
                    if (frame >= g.First && frame <= g.Last) { inside = g; return; }
                if (holdEnds)
                {
                    nearest = frame < groups[0].First ? groups[0].Keys[0] : groups[groups.Count - 1].Keys[groups[groups.Count - 1].Keys.Count - 1];
                    weight = 1f;
                    return;
                }
                foreach (Group g in groups)
                {
                    if (frame > g.Last)
                    {
                        Key k = g.Keys[g.Keys.Count - 1];
                        int d = frame - g.Last;
                        float w = d > k.EaseOut ? 0f : Smooth(1f - d / (k.EaseOut + 1f));
                        if (w > weight) { weight = w; nearest = k; }
                    }
                    else if (frame < g.First)
                    {
                        Key k = g.Keys[0];
                        int d = g.First - frame;
                        float w = d > k.EaseIn ? 0f : Smooth(1f - d / (k.EaseIn + 1f));
                        if (w > weight) { weight = w; nearest = k; }
                    }
                }
            }

            /// <summary>
            /// One curve per value through the group's keys, its tangents set here (not through Unity's tangent
            /// modes) so each segment is exactly what the earlier key's easing names: Linear, the segment's slope at
            /// both ends; Hold, a step (infinite out tangent); Smooth, the monotone cubic's tangents
            /// (<see cref="SmoothSlope"/>).
            /// </summary>
            static AnimationCurve[] BuildCurves(Group g)
            {
                int channels = g.Keys[0].Values.Length;
                int n = g.Keys.Count;
                var curves = new AnimationCurve[channels];
                for (int c = 0; c < channels; c++)
                {
                    var keys = new Keyframe[n];
                    for (int i = 0; i < n; i++) keys[i] = new Keyframe(g.Keys[i].Frame, g.Keys[i].Values[c]);
                    for (int i = 0; i < n - 1; i++)
                    {
                        float slope = (keys[i + 1].value - keys[i].value) / (keys[i + 1].time - keys[i].time);
                        switch (g.Keys[i].Easing)
                        {
                            case PoseEasing.Linear:
                                keys[i].outTangent = slope;
                                keys[i + 1].inTangent = slope;
                                break;
                            case PoseEasing.Hold:
                                keys[i].outTangent = float.PositiveInfinity;
                                keys[i + 1].inTangent = float.PositiveInfinity;
                                break;
                            default:
                                keys[i].outTangent = SmoothSlope(keys, i);
                                keys[i + 1].inTangent = SmoothSlope(keys, i + 1);
                                break;
                        }
                    }
                    curves[c] = new AnimationCurve(keys);
                }
                return curves;
            }

            /// <summary>
            /// The slope at key <paramref name="i"/> of a monotone cubic through the keys (Fritsch–Carlson / PCHIP):
            /// 0 at the first and last key and wherever the key is a peak or a dip, else the weighted harmonic mean of
            /// the slopes either side. Between two keys the curve then never goes past either of them.
            /// </summary>
            static float SmoothSlope(Keyframe[] keys, int i)
            {
                if (i <= 0 || i >= keys.Length - 1) return 0f;
                float hl = keys[i].time - keys[i - 1].time, hr = keys[i + 1].time - keys[i].time;
                float dl = (keys[i].value - keys[i - 1].value) / hl, dr = (keys[i + 1].value - keys[i].value) / hr;
                if (dl * dr <= 0f) return 0f;
                float w1 = 2f * hr + hl, w2 = hr + 2f * hl;
                return (w1 + w2) / (w1 / dl + w2 / dr);
            }

            static float Smooth(float t) => t * t * (3f - 2f * t);
        }
    }
}
