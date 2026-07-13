using System;
using System.Collections.Generic;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools.Fixtures
{
    [Serializable]
    public struct ManageScriptableObjectNestedData
    {
        public string note;
    }

    // Polymorphic [SerializeReference] hierarchy for managed-reference patch tests.
    public interface IManageScriptableObjectFeature { }

    [Serializable]
    public class ManageScriptableObjectDamageFeature : IManageScriptableObjectFeature
    {
        public int amount;
    }

    [Serializable]
    public class ManageScriptableObjectHealFeature : IManageScriptableObjectFeature
    {
        public float rate;
    }

    // NOTE: File name matches class name so Unity can resolve a MonoScript asset for this ScriptableObject type.
    public class ManageScriptableObjectTestDefinition : ManageScriptableObjectTestDefinitionBase
    {
        [SerializeField] private string displayName;
        [SerializeField] private List<Material> materials = new();
        [SerializeField] private ManageScriptableObjectNestedData nested;
        [SerializeReference] private List<IManageScriptableObjectFeature> features = new();

        public string DisplayName => displayName;
        public IReadOnlyList<Material> Materials => materials;
        public string NestedNote => nested.note;
        public IReadOnlyList<IManageScriptableObjectFeature> Features => features;
    }
}



