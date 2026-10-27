#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Re-targets the Transform animation of NON-humanoid bones (ears, tails, hair...) from an
/// original avatar onto a normalized copy whose bones have zero rest rotation.
/// Humanoid bones are skipped: Unity's humanoid retargeting already handles them.
///
/// Maths: for a bone with parent P, F = inverse(P_target_world) * P_source_world (both in T-pose).
/// F converts "rotation in the source parent's axes" to "rotation in the target parent's axes", so
///     target = F * (source * inverse(sourceRest)) * inverse(F) * targetRest
/// keeps the visual motion identical even though the bone axes differ between the two rigs.
/// </summary>
public static class AnimationNormalizer
{
    public class Options
    {
        public float sampleRate;            // 0 = use the clip's own frame rate
        public bool enforceTPose = true;    // put both rigs in the humanoid T-pose before reading rest values
        public bool keepOtherCurves = true; // copy muscle / blendshape / event data into the new clip
    }

    // One animated non-humanoid bone, with everything needed to convert it.
    private class Bone
    {
        public Transform src, dst;
        public string dstPath;
        public bool rot, pos, scl;                 // channels the clip animates
        public Quaternion srcRot, dstRot, parentMap, lastRot;
        public Vector3 srcPos, dstPos, srcScale, dstScale;
        public Matrix4x4 posMap;                   // source parent space -> target parent space
        public readonly AnimationCurve[] rotCurves = NewCurves(4), posCurves = NewCurves(3), sclCurves = NewCurves(3);

        public void Record(float time)
        {
            if (rot)
            {
                Quaternion delta = parentMap * (src.localRotation * Quaternion.Inverse(srcRot)) * Quaternion.Inverse(parentMap);
                Quaternion q = delta * dstRot;
                if (Quaternion.Dot(lastRot, q) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w); // keep curves continuous
                lastRot = q;
                Add(rotCurves, time, q.x, q.y, q.z, q.w);
            }
            if (pos)
            {
                Vector3 p = dstPos + posMap.MultiplyVector(src.localPosition - srcPos);
                Add(posCurves, time, p.x, p.y, p.z);
            }
            if (scl)
            {
                Vector3 s = src.localScale;
                Add(sclCurves, time, Rel(s.x, srcScale.x, dstScale.x), Rel(s.y, srcScale.y, dstScale.y), Rel(s.z, srcScale.z, dstScale.z));
            }
        }
    }

    public static AnimationClip Bake(GameObject sourceModel, GameObject targetModel, AnimationClip clip, Options opt)
    {
        GameObject src = Spawn(sourceModel), dst = null;
        try
        {
            dst = Spawn(targetModel);
            Animator sa = RequireHumanoid(src, "source model");
            Animator da = RequireHumanoid(dst, "normalized model");

            if (opt.enforceTPose)
            {
                EnterTPose(sa);
                EnterTPose(da);
            }

            List<Bone> bones = CollectBones(sa, da, clip);
            if (bones.Count == 0)
                throw new InvalidOperationException("No animated non-humanoid bones from the clip could be matched on the normalized model.");

            float rate = opt.sampleRate > 0f ? opt.sampleRate : clip.frameRate;
            Sample(sa.gameObject, clip, rate, bones);
            return Save(clip, bones, rate, opt);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (src) UnityEngine.Object.DestroyImmediate(src);
            if (dst) UnityEngine.Object.DestroyImmediate(dst);
        }
    }

    // ---------------------------------------------------------------- setup

    private static GameObject Spawn(GameObject model)
    {
        GameObject go = UnityEngine.Object.Instantiate(model);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        return go;
    }

    private static Animator RequireHumanoid(GameObject go, string label)
    {
        Animator a = go.GetComponentInChildren<Animator>();
        if (a == null || !a.isHuman)
            throw new InvalidOperationException($"The {label} needs an Animator with a Humanoid avatar.");
        return a;
    }

    // Humanoid muscle value 0 everywhere == the T-pose both rigs share.
    private static void EnterTPose(Animator animator)
    {
        var handler = new HumanPoseHandler(animator.avatar, animator.transform);
        var pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        pose.bodyRotation = Quaternion.identity;
        Array.Clear(pose.muscles, 0, pose.muscles.Length);
        handler.SetHumanPose(ref pose);
        handler.Dispose();
    }

    private static HashSet<Transform> HumanoidBones(Animator a)
    {
        var set = new HashSet<Transform>();
        for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
        {
            Transform t = a.GetBoneTransform((HumanBodyBones)i);
            if (t != null) set.Add(t);
        }
        return set;
    }

    // ---------------------------------------------------------------- bone matching

    private static List<Bone> CollectBones(Animator sa, Animator da, AnimationClip clip)
    {
        HashSet<Transform> srcHuman = HumanoidBones(sa), dstHuman = HumanoidBones(da);

        // Fallback lookup by name; duplicated names map to null so they are never guessed.
        var dstByName = new Dictionary<string, Transform>();
        foreach (Transform t in da.GetComponentsInChildren<Transform>(true))
            dstByName[t.name] = dstByName.ContainsKey(t.name) ? null : t;

        var bones = new Dictionary<Transform, Bone>();
        foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
        {
            if (b.type != typeof(Transform) || string.IsNullOrEmpty(b.path)) continue;

            Transform src = sa.transform.Find(b.path);
            if (src == null || srcHuman.Contains(src)) continue;

            if (!bones.TryGetValue(src, out Bone bone))
            {
                Transform dst = da.transform.Find(b.path);
                if (dst == null) dstByName.TryGetValue(src.name, out dst);

                if (dst == null || dstHuman.Contains(dst))
                    Debug.LogWarning($"[AnimationNormalizer] No match on the normalized model for '{b.path}', skipped.");
                else
                    bone = CreateBone(sa, da, src, dst);

                bones[src] = bone; // null remembers failures so we only warn once
            }
            if (bone == null) continue;

            string p = b.propertyName;
            bone.rot |= p.StartsWith("m_LocalRotation") || p.StartsWith("localEulerAnglesRaw");
            bone.pos |= p.StartsWith("m_LocalPosition");
            bone.scl |= p.StartsWith("m_LocalScale");
        }

        var result = new List<Bone>();
        foreach (Bone b in bones.Values)
            if (b != null) result.Add(b);
        return result;
    }

    // Captures rest values. Must run before the clip is sampled.
    private static Bone CreateBone(Animator sa, Animator da, Transform src, Transform dst)
    {
        return new Bone
        {
            src = src,
            dst = dst,
            dstPath = AnimationUtility.CalculateTransformPath(dst, da.transform),
            srcRot = src.localRotation,
            dstRot = dst.localRotation,
            srcPos = src.localPosition,
            dstPos = dst.localPosition,
            srcScale = src.localScale,
            dstScale = dst.localScale,
            parentMap = Quaternion.Inverse(dst.parent.rotation) * src.parent.rotation,
            posMap = dst.parent.worldToLocalMatrix * src.parent.localToWorldMatrix,
        };
    }

    // ---------------------------------------------------------------- sampling & output

    private static void Sample(GameObject root, AnimationClip clip, float rate, List<Bone> bones)
    {
        int frames = Mathf.Max(1, Mathf.CeilToInt(clip.length * rate));
        for (int i = 0; i <= frames; i++)
        {
            if (i % 20 == 0)
                EditorUtility.DisplayProgressBar("Normalizing animation", clip.name, i / (float)frames);

            clip.SampleAnimation(root, Mathf.Min(i / rate, clip.length));
            foreach (Bone b in bones) b.Record(Mathf.Min(i / rate, clip.length));
        }
    }

    private static AnimationClip Save(AnimationClip clip, List<Bone> bones, float rate, Options opt)
    {
        var result = new AnimationClip { name = clip.name + "_Normalized", frameRate = rate };

        if (opt.keepOtherCurves)
        {
            foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
                if (b.type != typeof(Transform))
                    AnimationUtility.SetEditorCurve(result, b, AnimationUtility.GetEditorCurve(clip, b));

            foreach (EditorCurveBinding b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                AnimationUtility.SetObjectReferenceCurve(result, b, AnimationUtility.GetObjectReferenceCurve(clip, b));

            AnimationUtility.SetAnimationEvents(result, AnimationUtility.GetAnimationEvents(clip));
        }

        foreach (Bone b in bones)
        {
            if (b.rot) SetCurves(result, b.dstPath, "m_LocalRotation", b.rotCurves, "xyzw");
            if (b.pos) SetCurves(result, b.dstPath, "m_LocalPosition", b.posCurves, "xyz");
            if (b.scl) SetCurves(result, b.dstPath, "m_LocalScale", b.sclCurves, "xyz");
        }

        if (opt.keepOtherCurves)
            AnimationUtility.SetAnimationClipSettings(result, AnimationUtility.GetAnimationClipSettings(clip));

        string srcAsset = AssetDatabase.GetAssetPath(clip);
        string dir = string.IsNullOrEmpty(srcAsset) ? "Assets" : Path.GetDirectoryName(srcAsset).Replace('\\', '/');
        if (!dir.StartsWith("Assets")) dir = "Assets";

        string path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{result.name}.anim");
        AssetDatabase.CreateAsset(result, path);
        AssetDatabase.SaveAssets();

        Debug.Log($"[AnimationNormalizer] Converted {bones.Count} non-humanoid bones -> {path}");
        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static AnimationCurve[] NewCurves(int count)
    {
        var curves = new AnimationCurve[count];
        for (int i = 0; i < count; i++) curves[i] = new AnimationCurve();
        return curves;
    }

    private static void Add(AnimationCurve[] curves, float time, params float[] values)
    {
        for (int i = 0; i < values.Length; i++) curves[i].AddKey(time, values[i]);
    }

    // Scale relative to rest: target = targetRest * (value / sourceRest). A zero rest scale uses the value as-is.
    private static float Rel(float value, float srcRest, float dstRest)
    {
        return Mathf.Abs(srcRest) > 1e-6f ? dstRest * value / srcRest : value;
    }

    private static void SetCurves(AnimationClip clip, string path, string property, AnimationCurve[] curves, string axes)
    {
        for (int i = 0; i < curves.Length; i++)
        {
            // Linear tangents so the dense samples play back exactly as sampled.
            Keyframe[] k = curves[i].keys;
            for (int j = 0; j < k.Length; j++)
            {
                k[j].inTangent = j > 0 ? Slope(k[j - 1], k[j]) : 0f;
                k[j].outTangent = j < k.Length - 1 ? Slope(k[j], k[j + 1]) : 0f;
            }
            curves[i].keys = k;

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(path, typeof(Transform), property + "." + axes[i]),
                curves[i]);
        }
    }

    private static float Slope(Keyframe a, Keyframe b)
    {
        return (b.value - a.value) / (b.time - a.time);
    }
}
#endif
