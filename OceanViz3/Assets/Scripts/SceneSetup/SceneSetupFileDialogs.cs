using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace OceanViz3
{
    /// <summary>
    /// Opens the project-owned scene setup browser from the running game interface.
    /// It selects one <c>.ov3scene</c> file and reports it through a callback.
    /// </summary>
    public sealed class SceneSetupFileDialogs
    {
        public const string SceneSetupExtension = "ov3scene";

        private readonly SceneSetupFileBrowser browser;

        public SceneSetupFileDialogs(VisualElement parent)
        {
            Debug.Assert(parent != null, "[SceneSetupFileDialogs] A parent visual element is required.");
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            browser = new SceneSetupFileBrowser(parent, SceneSetupExtension);
        }

        public void ShowSavePath(string initialDirectory, Action<string> pathSelected)
        {
            Debug.Assert(pathSelected != null, "[SceneSetupFileDialogs] Save callback is required.");
            if (pathSelected == null)
            {
                throw new ArgumentNullException(nameof(pathSelected));
            }

            browser.ShowSave(initialDirectory, pathSelected);
        }

        public void ShowOpenPath(string initialDirectory, Action<string> pathSelected)
        {
            Debug.Assert(pathSelected != null, "[SceneSetupFileDialogs] Open callback is required.");
            if (pathSelected == null)
            {
                throw new ArgumentNullException(nameof(pathSelected));
            }

            browser.ShowOpen(initialDirectory, pathSelected);
        }

        public bool Close()
        {
            return browser.Close();
        }
    }
}
