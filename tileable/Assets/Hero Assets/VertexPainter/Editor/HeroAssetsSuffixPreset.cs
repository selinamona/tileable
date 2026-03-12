using System;
using UnityEngine;

namespace HeroAssets
{
    [Serializable]
    public struct SuffixesData
    {
        public string baseColor;
        public string normal;
        public string unityMaskMap;
        public string height;

        public string arm;
        public string orm;
        public string metallic;
        public string ao;
        public string smoothness;
        public string roughness;

        public void NormalizeNulls()
        {
            baseColor ??= "";
            normal ??= "";
            unityMaskMap ??= "";
            height ??= "";

            arm ??= "";
            orm ??= "";
            metallic ??= "";
            ao ??= "";
            smoothness ??= "";
            roughness ??= "";
        }

        public bool EqualsTo(in SuffixesData other)
        {
            // Compare normalized
            var a = this; a.NormalizeNulls();
            var b = other; b.NormalizeNulls();

            return string.Equals(a.baseColor, b.baseColor, StringComparison.Ordinal) &&
                   string.Equals(a.normal, b.normal, StringComparison.Ordinal) &&
                   string.Equals(a.unityMaskMap, b.unityMaskMap, StringComparison.Ordinal) &&
                   string.Equals(a.height, b.height, StringComparison.Ordinal) &&
                   string.Equals(a.arm, b.arm, StringComparison.Ordinal) &&
                   string.Equals(a.orm, b.orm, StringComparison.Ordinal) &&
                   string.Equals(a.metallic, b.metallic, StringComparison.Ordinal) &&
                   string.Equals(a.ao, b.ao, StringComparison.Ordinal) &&
                   string.Equals(a.smoothness, b.smoothness, StringComparison.Ordinal) &&
                   string.Equals(a.roughness, b.roughness, StringComparison.Ordinal);
        }
    }

    [CreateAssetMenu(menuName = "Hero Assets/Texture Manager/Suffix Preset", fileName = "SuffixPreset_")]
    public class HeroAssetsSuffixPreset : ScriptableObject
    {
        [Header("Display")]
        public string displayName = "New Preset";
        [TextArea(2, 4)]
        public string description = "";

        [Header("Ordering / Category")]
        public bool isBuiltIn = false;

        [Tooltip("Built-in presets are sorted by this (ascending). If equal, alphabetical by displayName.")]
        public int sortOrder = 0;

        [Header("Suffixes")]
        public SuffixesData suffixes;
    }
}