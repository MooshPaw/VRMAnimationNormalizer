#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

/// <summary>Small editor window in front of <see cref="AnimationNormalizer"/>.</summary>
public class AnimationNormalizerWindow : EditorWindow
{
    [SerializeField] private GameObject sourceModel;
    [SerializeField] private GameObject normalizedModel;
    [SerializeField] private AnimationClip clip;
    [SerializeField] private float sampleRate;
    [SerializeField] private bool enforceTPose = true;
    [SerializeField] private bool keepOtherCurves = true;

    [MenuItem("Tools/Animation Normalizer")]
    private static void Open()
    {
        GetWindow<AnimationNormalizerWindow>("Animation Normalizer");
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Converts the non-humanoid bone animation (ears, tails, hair...) of an original avatar " +
            "so it plays correctly on its normalized (zero rest rotation) copy.",
            MessageType.Info);

        sourceModel = (GameObject)EditorGUILayout.ObjectField("Original Model", sourceModel, typeof(GameObject), true);
        normalizedModel = (GameObject)EditorGUILayout.ObjectField("Normalized Model", normalizedModel, typeof(GameObject), true);
        clip = (AnimationClip)EditorGUILayout.ObjectField("Animation", clip, typeof(AnimationClip), false);

        EditorGUILayout.Space();
        sampleRate = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Sample Rate", "0 = use the clip's frame rate"), sampleRate));
        enforceTPose = EditorGUILayout.Toggle(new GUIContent("Enforce T-Pose", "Read rest values with both rigs in the humanoid T-pose"), enforceTPose);
        keepOtherCurves = EditorGUILayout.Toggle(new GUIContent("Keep Other Curves", "Copy muscle, blendshape and event data into the new clip"), keepOtherCurves);

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(sourceModel == null || normalizedModel == null || clip == null))
        {
            if (GUILayout.Button("Create Normalized Animation", GUILayout.Height(32)))
                Run();
        }
    }

    private void Run()
    {
        try
        {
            AnimationClip result = AnimationNormalizer.Bake(sourceModel, normalizedModel, clip, new AnimationNormalizer.Options
            {
                sampleRate = sampleRate,
                enforceTPose = enforceTPose,
                keepOtherCurves = keepOtherCurves,
            });

            Selection.activeObject = result;
            EditorGUIUtility.PingObject(result);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog("Animation Normalizer", e.Message, "OK");
        }
    }
}
#endif
