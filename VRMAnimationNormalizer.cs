using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class VRMAnimationNormalizer : EditorWindow
{
    [SerializeField] private GameObject sourceModel;
    [SerializeField] private GameObject targetModel;
    [SerializeField] private AnimationClip sourceAnimation;

    [SerializeField] private int sampleRate = 60;
    [SerializeField] private bool transferPosition = false;
    [SerializeField] private bool transferScale = false;
    [SerializeField] private bool debugSampling = true;

    [MenuItem("Tools/VRM/Normalize Animation")]
    private static void Open()
    {
        GetWindow<VRMAnimationNormalizer>(
            "VRM Animation Normalizer");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(
            "VRM Animation Normalizer",
            EditorStyles.boldLabel);

        EditorGUILayout.Space();

        EditorGUILayout.HelpBox(
            "Transfers animated non-Humanoid bones from an original " +
            "model to a normalized VRM using rest-pose offsets.",
            MessageType.Info);

        sourceModel = (GameObject)EditorGUILayout.ObjectField(
            "Source Model",
            sourceModel,
            typeof(GameObject),
            true);

        targetModel = (GameObject)EditorGUILayout.ObjectField(
            "Normalized VRM",
            targetModel,
            typeof(GameObject),
            true);

        sourceAnimation = (AnimationClip)EditorGUILayout.ObjectField(
            "Animation",
            sourceAnimation,
            typeof(AnimationClip),
            false);

        EditorGUILayout.Space();

        sampleRate = EditorGUILayout.IntField(
            "Sample Rate",
            sampleRate);

        sampleRate = Mathf.Clamp(sampleRate, 1, 240);

        transferPosition = EditorGUILayout.Toggle(
            "Transfer Position",
            transferPosition);

        transferScale = EditorGUILayout.Toggle(
            "Transfer Scale",
            transferScale);

        debugSampling = EditorGUILayout.Toggle(
            "Debug Sampling",
            debugSampling);

        EditorGUILayout.Space();

        if (sourceModel == null ||
            targetModel == null ||
            sourceAnimation == null)
        {
            EditorGUILayout.HelpBox(
                "Assign the source model, normalized VRM and animation.",
                MessageType.Warning);
        }

        EditorGUILayout.Space();

        GUI.enabled =
            sourceModel != null &&
            targetModel != null &&
            sourceAnimation != null;

        if (GUILayout.Button(
                "Bake Normalized Animation",
                GUILayout.Height(35)))
        {
            Bake();
        }

        GUI.enabled = true;
    }

    private class BonePair
    {
        public Transform source;
        public Transform target;

        public Quaternion sourceRestRotation;
        public Quaternion targetRestRotation;

        public Vector3 sourceRestPosition;
        public Vector3 targetRestPosition;

        public Vector3 sourceRestScale;
        public Vector3 targetRestScale;

        public string sourcePath;
        public string targetPath;
    }

    private void Bake()
    {
        if (sourceModel == null ||
            targetModel == null ||
            sourceAnimation == null)
        {
            EditorUtility.DisplayDialog(
                "Error",
                "Please assign all three objects.",
                "OK");

            return;
        }

        if (sampleRate < 1)
            sampleRate = 60;

        GameObject sourceInstance = null;
        GameObject targetInstance = null;

        try
        {
            /*
             * ---------------------------------------------------------
             * CREATE TEMPORARY INSTANCES
             * ---------------------------------------------------------
             */

            sourceInstance =
                Instantiate(sourceModel);

            targetInstance =
                Instantiate(targetModel);

            sourceInstance.name =
                "__VRMNormalizer_Source";

            targetInstance.name =
                "__VRMNormalizer_Target";

            sourceInstance.hideFlags =
                HideFlags.HideAndDontSave;

            targetInstance.hideFlags =
                HideFlags.HideAndDontSave;

            /*
             * Put both models into the same world transform.
             */
            sourceInstance.transform.position =
                Vector3.zero;

            sourceInstance.transform.rotation =
                Quaternion.identity;

            sourceInstance.transform.localScale =
                Vector3.one;

            targetInstance.transform.position =
                Vector3.zero;

            targetInstance.transform.rotation =
                Quaternion.identity;

            targetInstance.transform.localScale =
                Vector3.one;

            /*
             * ---------------------------------------------------------
             * FIND ANIMATORS
             * ---------------------------------------------------------
             */

            Animator sourceAnimator =
                sourceInstance.GetComponentInChildren<Animator>();

            Animator targetAnimator =
                targetInstance.GetComponentInChildren<Animator>();

            if (sourceAnimator == null)
            {
                throw new Exception(
                    "The source model does not contain an Animator.");
            }

            if (targetAnimator == null)
            {
                throw new Exception(
                    "The target VRM does not contain an Animator.");
            }

            if (!sourceAnimator.isHuman)
            {
                throw new Exception(
                    "The source model must use a Humanoid Avatar.");
            }

            if (!targetAnimator.isHuman)
            {
                throw new Exception(
                    "The target VRM must use a Humanoid Avatar.");
            }

            /*
             * ---------------------------------------------------------
             * FIND ANIMATED SOURCE BONES
             * ---------------------------------------------------------
             */

            HashSet<Transform> affectedBones =
                FindAffectedBones(
                    sourceInstance,
                    sourceAnimation);

            Debug.Log(
                $"VRM Normalizer: animation contains " +
                $"{AnimationUtility.GetCurveBindings(sourceAnimation).Length} " +
                $"curve bindings.");

            Debug.Log(
                $"VRM Normalizer: found " +
                $"{affectedBones.Count} affected Transform bones.");

            if (debugSampling)
            {
                foreach (Transform bone in affectedBones)
                {
                    Debug.Log(
                        "Affected bone: " +
                        GetPath(
                            sourceInstance.transform,
                            bone));
                }
            }

            /*
             * ---------------------------------------------------------
             * MAP SOURCE BONES TO TARGET BONES
             * ---------------------------------------------------------
             */

            List<BonePair> pairs =
                BuildBonePairs(
                    sourceAnimator,
                    targetAnimator,
                    affectedBones);

            Debug.Log(
                $"VRM Normalizer: mapped " +
                $"{pairs.Count} non-Humanoid bones.");

            if (pairs.Count == 0)
            {
                throw new Exception(
                    "No animated non-Humanoid bones could be mapped " +
                    "to the target VRM.");
            }

            /*
             * ---------------------------------------------------------
             * CAPTURE REST POSE
             * ---------------------------------------------------------
             *
             * This must happen BEFORE sampling the animation.
             */

            CaptureRestPose(pairs);

            /*
             * ---------------------------------------------------------
             * CREATE OUTPUT CLIP
             * ---------------------------------------------------------
             */

            AnimationClip output =
                new AnimationClip();

            output.name =
                sourceAnimation.name +
                "_Normalized";

            output.frameRate =
                sampleRate;

            /*
             * ---------------------------------------------------------
             * CREATE CURVE STORAGE
             * ---------------------------------------------------------
             */

            Dictionary<string, AnimationCurve[]> rotationCurves =
                new Dictionary<string, AnimationCurve[]>();

            Dictionary<string, AnimationCurve[]> positionCurves =
                new Dictionary<string, AnimationCurve[]>();

            Dictionary<string, AnimationCurve[]> scaleCurves =
                new Dictionary<string, AnimationCurve[]>();

            foreach (BonePair pair in pairs)
            {
                rotationCurves[pair.targetPath] =
                    new AnimationCurve[]
                    {
                        new AnimationCurve(),
                        new AnimationCurve(),
                        new AnimationCurve(),
                        new AnimationCurve()
                    };

                if (transferPosition)
                {
                    positionCurves[pair.targetPath] =
                        new AnimationCurve[]
                        {
                            new AnimationCurve(),
                            new AnimationCurve(),
                            new AnimationCurve()
                        };
                }

                if (transferScale)
                {
                    scaleCurves[pair.targetPath] =
                        new AnimationCurve[]
                        {
                            new AnimationCurve(),
                            new AnimationCurve(),
                            new AnimationCurve()
                        };
                }
            }

            /*
             * ---------------------------------------------------------
             * SAMPLE ANIMATION
             * ---------------------------------------------------------
             */

            int frameCount =
                Mathf.CeilToInt(
                    sourceAnimation.length *
                    sampleRate) + 1;

            for (int frame = 0;
                 frame < frameCount;
                 frame++)
            {
                float time =
                    Mathf.Min(
                        frame / (float)sampleRate,
                        sourceAnimation.length);

                EditorUtility.DisplayProgressBar(
                    "Baking VRM Animation",
                    $"Sampling frame {frame + 1} / {frameCount}",
                    frame / (float)Mathf.Max(
                        1,
                        frameCount - 1));

                /*
                 * -----------------------------------------------------
                 * SAMPLE THE ORIGINAL ANIMATION
                 * -----------------------------------------------------
                 *
                 * AnimationClip.SampleAnimation directly evaluates
                 * the animation on the supplied GameObject.
                 */

                sourceAnimation.SampleAnimation(
                    sourceInstance,
                    time);

                /*
                 * -----------------------------------------------------
                 * DEBUG
                 * -----------------------------------------------------
                 */

                if (debugSampling &&
                    (frame == 0 ||
                     frame == frameCount / 2 ||
                     frame == frameCount - 1))
                {
                    foreach (BonePair pair in pairs)
                    {
                        Debug.Log(
                            $"{pair.source.name} @ {time:F3} | " +
                            $"Rest: {pair.sourceRestRotation.eulerAngles} | " +
                            $"Animated: {pair.source.localEulerAngles}");
                    }
                }

                /*
                 * -----------------------------------------------------
                 * CONVERT EACH BONE
                 * -----------------------------------------------------
                 */

                foreach (BonePair pair in pairs)
                {
                    /*
                     * -------------------------------------------------
                     * ROTATION
                     * -------------------------------------------------
                     *
                     * Calculate how much the source bone moved
                     * relative to its rest orientation.
                     *
                     *     Delta =
                     *         inverse(SourceRest)
                     *         * SourceAnimated
                     */

                    Quaternion rotationDelta =
                        Quaternion.Inverse(
                            pair.sourceRestRotation)
                        *
                        pair.source.localRotation;

                    /*
                     * Apply the same relative motion to the target
                     * bone's rest orientation.
                     *
                     *     TargetAnimated =
                     *         TargetRest * Delta
                     */

                    Quaternion targetRotation =
                        pair.targetRestRotation *
                        rotationDelta;

                    AddQuaternionKey(
                        rotationCurves[pair.targetPath],
                        time,
                        targetRotation);

                    /*
                     * -------------------------------------------------
                     * POSITION
                     * -------------------------------------------------
                     */

                    if (transferPosition)
                    {
                        Vector3 positionDelta =
                            pair.source.localPosition -
                            pair.sourceRestPosition;

                        Vector3 targetPosition =
                            pair.targetRestPosition +
                            positionDelta;

                        AddVectorKey(
                            positionCurves[pair.targetPath],
                            time,
                            targetPosition);
                    }

                    /*
                     * -------------------------------------------------
                     * SCALE
                     * -------------------------------------------------
                     */

                    if (transferScale)
                    {
                        Vector3 scaleDelta =
                            DivideSafe(
                                pair.source.localScale,
                                pair.sourceRestScale);

                        Vector3 targetScale =
                            Vector3.Scale(
                                pair.targetRestScale,
                                scaleDelta);

                        AddVectorKey(
                            scaleCurves[pair.targetPath],
                            time,
                            targetScale);
                    }
                }
            }

            /*
             * ---------------------------------------------------------
             * WRITE ROTATION CURVES
             * ---------------------------------------------------------
             */

            AddRotationCurves(
                output,
                rotationCurves);

            /*
             * ---------------------------------------------------------
             * WRITE POSITION CURVES
             * ---------------------------------------------------------
             */

            if (transferPosition)
            {
                AddVectorCurves(
                    output,
                    positionCurves,
                    "m_LocalPosition");
            }

            /*
             * ---------------------------------------------------------
             * WRITE SCALE CURVES
             * ---------------------------------------------------------
             */

            if (transferScale)
            {
                AddVectorCurves(
                    output,
                    scaleCurves,
                    "m_LocalScale");
            }

            /*
             * ---------------------------------------------------------
             * SAVE
             * ---------------------------------------------------------
             */

            string sourcePath =
                AssetDatabase.GetAssetPath(
                    sourceAnimation);

            string directory =
                "Assets";

            if (!string.IsNullOrEmpty(sourcePath))
            {
                directory =
                    System.IO.Path.GetDirectoryName(
                        sourcePath)
                    .Replace("\\", "/");

                if (string.IsNullOrEmpty(directory))
                    directory = "Assets";
            }

            string outputPath =
                AssetDatabase.GenerateUniqueAssetPath(
                    directory +
                    "/" +
                    sourceAnimation.name +
                    "_Normalized.anim");

            AssetDatabase.CreateAsset(
                output,
                outputPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();

            EditorGUIUtility.PingObject(
                output);

            Debug.Log(
                $"VRM Normalizer: created:\n{outputPath}");

            EditorUtility.DisplayDialog(
                "Bake Complete",
                "Created:\n\n" +
                outputPath +
                "\n\n" +
                $"Animated bones: {pairs.Count}",
                "OK");
        }
        catch (Exception exception)
        {
            EditorUtility.ClearProgressBar();

            Debug.LogException(
                exception);

            EditorUtility.DisplayDialog(
                "Bake Failed",
                exception.Message,
                "OK");
        }
        finally
        {
            if (sourceInstance != null)
                DestroyImmediate(
                    sourceInstance);

            if (targetInstance != null)
                DestroyImmediate(
                    targetInstance);
        }
    }

    /*
     * ================================================================
     * FIND AFFECTED BONES
     * ================================================================
     */

    private static HashSet<Transform> FindAffectedBones(
        GameObject root,
        AnimationClip clip)
    {
        HashSet<Transform> result =
            new HashSet<Transform>();

        Transform[] transforms =
            root.GetComponentsInChildren<Transform>(
                true);

        Dictionary<string, Transform> pathMap =
            new Dictionary<string, Transform>();

        foreach (Transform transform in transforms)
        {
            string path =
                AnimationUtility.CalculateTransformPath(
                    transform,
                    root.transform);

            pathMap[path] =
                transform;
        }

        EditorCurveBinding[] bindings =
            AnimationUtility.GetCurveBindings(
                clip);

        foreach (EditorCurveBinding binding in bindings)
        {
            /*
             * We only care about Transform animation.
             */

            if (binding.type != typeof(Transform))
                continue;

            if (pathMap.TryGetValue(
                    binding.path,
                    out Transform transform))
            {
                result.Add(
                    transform);
            }
        }

        return result;
    }

    /*
     * ================================================================
     * BUILD SOURCE -> TARGET BONE MAPPING
     * ================================================================
     */

    private static List<BonePair> BuildBonePairs(
        Animator sourceAnimator,
        Animator targetAnimator,
        HashSet<Transform> affectedBones)
    {
        List<BonePair> result =
            new List<BonePair>();

        Transform sourceRoot =
            sourceAnimator.transform;

        Transform targetRoot =
            targetAnimator.transform;

        /*
         * Build set of source Humanoid bones.
         *
         * These are intentionally excluded because Unity's
         * Humanoid retargeting should handle them.
         */

        HashSet<Transform> humanoidBones =
            new HashSet<Transform>();

        foreach (HumanBodyBones humanBone
                 in Enum.GetValues(
                     typeof(HumanBodyBones)))
        {
            if (humanBone ==
                HumanBodyBones.LastBone)
            {
                continue;
            }

            Transform transform =
                sourceAnimator.GetBoneTransform(
                    humanBone);

            if (transform != null)
            {
                humanoidBones.Add(
                    transform);
            }
        }

        /*
         * ------------------------------------------------------------
         * TARGET LOOKUP BY PATH
         * ------------------------------------------------------------
         */

        Dictionary<string, Transform> targetByPath =
            new Dictionary<string, Transform>();

        Dictionary<string, List<Transform>> targetByName =
            new Dictionary<string, List<Transform>>();

        foreach (Transform target
                 in targetRoot.GetComponentsInChildren<Transform>(
                     true))
        {
            string path =
                AnimationUtility.CalculateTransformPath(
                    target,
                    targetRoot);

            targetByPath[path] =
                target;

            if (!targetByName.ContainsKey(
                    target.name))
            {
                targetByName[target.name] =
                    new List<Transform>();
            }

            targetByName[target.name].Add(
                target);
        }

        /*
         * ------------------------------------------------------------
         * MATCH ONLY AFFECTED BONES
         * ------------------------------------------------------------
         */

        foreach (Transform source in affectedBones)
        {
            /*
             * Humanoid bones are handled by Unity.
             */

            if (humanoidBones.Contains(
                    source))
            {
                continue;
            }

            if (source == sourceRoot)
                continue;

            string sourcePath =
                AnimationUtility.CalculateTransformPath(
                    source,
                    sourceRoot);

            Transform target = null;

            /*
             * First attempt:
             * identical hierarchy path.
             */

            targetByPath.TryGetValue(
                sourcePath,
                out target);

            /*
             * Second attempt:
             * unique bone name.
             */

            if (target == null &&
                targetByName.TryGetValue(
                    source.name,
                    out List<Transform> candidates))
            {
                if (candidates.Count == 1)
                {
                    target =
                        candidates[0];
                }
            }

            if (target == null)
            {
                Debug.LogWarning(
                    "VRM Normalizer: Could not map " +
                    $"source bone '{source.name}' " +
                    $"(path: '{sourcePath}').");

                continue;
            }

            string targetPath =
                AnimationUtility.CalculateTransformPath(
                    target,
                    targetRoot);

            result.Add(
                new BonePair
                {
                    source = source,
                    target = target,

                    sourcePath =
                        sourcePath,

                    targetPath =
                        targetPath
                });
        }

        return result;
    }

    /*
     * ================================================================
     * REST POSE
     * ================================================================
     */

    private static void CaptureRestPose(
        List<BonePair> pairs)
    {
        foreach (BonePair pair in pairs)
        {
            pair.sourceRestRotation =
                pair.source.localRotation;

            pair.targetRestRotation =
                pair.target.localRotation;

            pair.sourceRestPosition =
                pair.source.localPosition;

            pair.targetRestPosition =
                pair.target.localPosition;

            pair.sourceRestScale =
                pair.source.localScale;

            pair.targetRestScale =
                pair.target.localScale;
        }
    }

    /*
     * ================================================================
     * PATH
     * ================================================================
     */

    private static string GetPath(
        Transform root,
        Transform target)
    {
        return AnimationUtility.CalculateTransformPath(
            target,
            root);
    }

    /*
     * ================================================================
     * ADD QUATERNION KEY
     * ================================================================
     */

    private static void AddQuaternionKey(
        AnimationCurve[] curves,
        float time,
        Quaternion value)
    {
        curves[0].AddKey(
            time,
            value.x);

        curves[1].AddKey(
            time,
            value.y);

        curves[2].AddKey(
            time,
            value.z);

        curves[3].AddKey(
            time,
            value.w);
    }

    /*
     * ================================================================
     * ADD VECTOR KEY
     * ================================================================
     */

    private static void AddVectorKey(
        AnimationCurve[] curves,
        float time,
        Vector3 value)
    {
        curves[0].AddKey(
            time,
            value.x);

        curves[1].AddKey(
            time,
            value.y);

        curves[2].AddKey(
            time,
            value.z);
    }

    /*
     * ================================================================
     * ROTATION CURVES
     * ================================================================
     */

    private static void AddRotationCurves(
        AnimationClip clip,
        Dictionary<string, AnimationCurve[]> curves)
    {
        foreach (KeyValuePair<
                     string,
                     AnimationCurve[]> pair
                 in curves)
        {
            string path =
                pair.Key;

            AnimationCurve[] curvesForBone =
                pair.Value;

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    path,
                    typeof(Transform),
                    "m_LocalRotation.x"),
                curvesForBone[0]);

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    path,
                    typeof(Transform),
                    "m_LocalRotation.y"),
                curvesForBone[1]);

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    path,
                    typeof(Transform),
                    "m_LocalRotation.z"),
                curvesForBone[2]);

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    path,
                    typeof(Transform),
                    "m_LocalRotation.w"),
                curvesForBone[3]);
        }
    }

    /*
     * ================================================================
     * VECTOR CURVES
     * ================================================================
     */

    private static void AddVectorCurves(
        AnimationClip clip,
        Dictionary<string, AnimationCurve[]> curves,
        string property)
    {
        foreach (KeyValuePair<
                     string,
                     AnimationCurve[]> pair
                 in curves)
        {
            string path =
                pair.Key;

            AnimationCurve[] curvesForBone =
                pair.Value;

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    path,
                    typeof(Transform),
                    property + ".x"),
                curvesForBone[0]);

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    path,
                    typeof(Transform),
                    property + ".y"),
                curvesForBone[1]);

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    path,
                    typeof(Transform),
                    property + ".z"),
                curvesForBone[2]);
        }
    }

    /*
     * ================================================================
     * SAFE VECTOR DIVISION
     * ================================================================
     */

    private static Vector3 DivideSafe(
        Vector3 a,
        Vector3 b)
    {
        return new Vector3(
            Mathf.Abs(b.x) > 0.000001f
                ? a.x / b.x
                : 1f,

            Mathf.Abs(b.y) > 0.000001f
                ? a.y / b.y
                : 1f,

            Mathf.Abs(b.z) > 0.000001f
                ? a.z / b.z
                : 1f);
    }
}