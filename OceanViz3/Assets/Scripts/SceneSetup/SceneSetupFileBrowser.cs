using System;
using System.IO;
using System.Security;
using UnityEngine;
using UnityEngine.UIElements;

namespace OceanViz3
{
    /// <summary>
    /// Project-owned runtime file browser limited to OceanViz scene setup files.
    /// It supports normal folder navigation without carrying a general-purpose file browser dependency.
    /// </summary>
    internal sealed class SceneSetupFileBrowser
    {
        private const string LastDirectoryPlayerPrefsKey = "OceanViz3.SceneSetup.LastDirectory";

        private enum BrowserMode
        {
            Save,
            Open
        }

        private readonly VisualElement overlay;
        private readonly Label titleLabel;
        private readonly TextField pathField;
        private readonly ScrollView entriesScrollView;
        private readonly Label emptyDirectoryLabel;
        private readonly Label errorLabel;
        private readonly VisualElement saveNameRow;
        private readonly TextField saveNameField;
        private readonly VisualElement overwriteRow;
        private readonly Label overwriteLabel;
        private readonly Button primaryButton;
        private readonly string requiredExtension;

        private BrowserMode mode;
        private string currentDirectory;
        private string defaultDirectory;
        private string selectedFilePath;
        private Action<string> pathSelected;
        private bool isOpen;

        public SceneSetupFileBrowser(VisualElement parent, string requiredExtension)
        {
            Debug.Assert(parent != null, "[SceneSetupFileBrowser] A parent visual element is required.");
            Debug.Assert(!string.IsNullOrEmpty(requiredExtension), "[SceneSetupFileBrowser] A file extension is required.");
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }
            if (string.IsNullOrEmpty(requiredExtension))
            {
                throw new ArgumentException("A file extension is required.", nameof(requiredExtension));
            }

            this.requiredExtension = "." + requiredExtension.TrimStart('.');

            overlay = new VisualElement();
            overlay.name = "SceneSetupFileBrowserOverlay";
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0;
            overlay.style.right = 0;
            overlay.style.top = 0;
            overlay.style.bottom = 0;
            overlay.style.alignItems = Align.Center;
            overlay.style.justifyContent = Justify.Center;
            overlay.style.backgroundColor = new Color(0.0f, 0.0f, 0.0f, 0.72f);
            overlay.style.display = DisplayStyle.None;

            VisualElement card = new VisualElement();
            card.name = "SceneSetupFileBrowserCard";
            card.style.width = Length.Percent(78.0f);
            card.style.maxWidth = 820.0f;
            card.style.minWidth = 440.0f;
            card.style.height = Length.Percent(76.0f);
            card.style.minHeight = 360.0f;
            card.style.paddingTop = 14.0f;
            card.style.paddingRight = 14.0f;
            card.style.paddingBottom = 14.0f;
            card.style.paddingLeft = 14.0f;
            card.style.backgroundColor = new Color(0.16f, 0.16f, 0.16f, 1.0f);
            card.style.borderTopWidth = 1.0f;
            card.style.borderRightWidth = 1.0f;
            card.style.borderBottomWidth = 1.0f;
            card.style.borderLeftWidth = 1.0f;
            Color borderColor = new Color(0.36f, 0.36f, 0.36f, 1.0f);
            card.style.borderTopColor = borderColor;
            card.style.borderRightColor = borderColor;
            card.style.borderBottomColor = borderColor;
            card.style.borderLeftColor = borderColor;
            overlay.Add(card);

            titleLabel = new Label();
            titleLabel.style.fontSize = 17.0f;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 10.0f;
            card.Add(titleLabel);

            VisualElement navigationRow = CreateHorizontalRow();
            navigationRow.style.marginBottom = 7.0f;
            card.Add(navigationRow);

            Button upButton = new Button(NavigateUp);
            upButton.text = "Up";
            upButton.tooltip = "Open the parent folder";
            SetCompactButtonWidth(upButton, 54.0f);
            navigationRow.Add(upButton);

            Button homeButton = new Button(NavigateHome);
            homeButton.text = "Saved Scenes";
            homeButton.tooltip = "Return to the default SavedScenes folder";
            SetCompactButtonWidth(homeButton, 102.0f);
            navigationRow.Add(homeButton);

            Button rootsButton = new Button(ShowRoots);
            rootsButton.text = "Roots";
            rootsButton.tooltip = "Show available drives or filesystem roots";
            SetCompactButtonWidth(rootsButton, 62.0f);
            navigationRow.Add(rootsButton);

            pathField = new TextField();
            pathField.name = "SceneSetupFileBrowserPath";
            SetFlexibleTextField(pathField);
            pathField.style.marginLeft = 5.0f;
            navigationRow.Add(pathField);

            Button goButton = new Button(NavigateToTypedPath);
            goButton.text = "Go";
            SetCompactButtonWidth(goButton, 42.0f);
            navigationRow.Add(goButton);
            pathField.RegisterCallback<KeyDownEvent>(OnPathFieldKeyDown);

            entriesScrollView = new ScrollView(ScrollViewMode.Vertical);
            entriesScrollView.name = "SceneSetupFileBrowserEntries";
            entriesScrollView.style.flexGrow = 1.0f;
            entriesScrollView.style.backgroundColor = new Color(0.105f, 0.105f, 0.105f, 1.0f);
            entriesScrollView.style.borderTopWidth = 1.0f;
            entriesScrollView.style.borderRightWidth = 1.0f;
            entriesScrollView.style.borderBottomWidth = 1.0f;
            entriesScrollView.style.borderLeftWidth = 1.0f;
            Color entriesBorderColor = new Color(0.08f, 0.08f, 0.08f, 1.0f);
            entriesScrollView.style.borderTopColor = entriesBorderColor;
            entriesScrollView.style.borderRightColor = entriesBorderColor;
            entriesScrollView.style.borderBottomColor = entriesBorderColor;
            entriesScrollView.style.borderLeftColor = entriesBorderColor;
            card.Add(entriesScrollView);

            emptyDirectoryLabel = new Label();
            emptyDirectoryLabel.text = "No folders or scene setup files here.";
            emptyDirectoryLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            emptyDirectoryLabel.style.paddingTop = 18.0f;
            emptyDirectoryLabel.style.display = DisplayStyle.None;
            entriesScrollView.Add(emptyDirectoryLabel);

            errorLabel = new Label();
            errorLabel.name = "SceneSetupFileBrowserError";
            errorLabel.style.color = new Color(1.0f, 0.54f, 0.45f, 1.0f);
            errorLabel.style.whiteSpace = WhiteSpace.Normal;
            errorLabel.style.marginTop = 5.0f;
            errorLabel.style.display = DisplayStyle.None;
            card.Add(errorLabel);

            saveNameRow = CreateHorizontalRow();
            saveNameRow.style.marginTop = 7.0f;
            Label saveNameLabel = new Label("File name");
            saveNameLabel.style.width = 72.0f;
            saveNameLabel.style.flexShrink = 0.0f;
            saveNameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            saveNameRow.Add(saveNameLabel);
            saveNameField = new TextField();
            SetFlexibleTextField(saveNameField);
            saveNameRow.Add(saveNameField);
            card.Add(saveNameRow);
            saveNameField.RegisterValueChangedCallback(OnSaveNameChanged);
            saveNameField.RegisterCallback<KeyDownEvent>(OnSaveNameKeyDown);

            overwriteRow = CreateHorizontalRow();
            overwriteRow.style.marginTop = 7.0f;
            overwriteRow.style.display = DisplayStyle.None;
            overwriteLabel = new Label();
            overwriteLabel.style.flexGrow = 1.0f;
            overwriteLabel.style.flexShrink = 1.0f;
            overwriteLabel.style.minWidth = 0.0f;
            overwriteLabel.style.whiteSpace = WhiteSpace.Normal;
            overwriteRow.Add(overwriteLabel);
            Button overwriteButton = new Button(ConfirmOverwrite);
            overwriteButton.text = "Overwrite";
            SetCompactButtonWidth(overwriteButton, 82.0f);
            overwriteRow.Add(overwriteButton);
            Button keepButton = new Button(CancelOverwrite);
            keepButton.text = "Keep Existing";
            SetCompactButtonWidth(keepButton, 102.0f);
            overwriteRow.Add(keepButton);
            card.Add(overwriteRow);

            VisualElement actionRow = CreateHorizontalRow();
            actionRow.style.justifyContent = Justify.FlexEnd;
            actionRow.style.marginTop = 10.0f;
            card.Add(actionRow);

            Button cancelButton = new Button(CloseWithoutSelection);
            cancelButton.text = "Cancel";
            SetCompactButtonWidth(cancelButton, 78.0f);
            actionRow.Add(cancelButton);

            primaryButton = new Button(ConfirmSelection);
            SetCompactButtonWidth(primaryButton, 88.0f);
            actionRow.Add(primaryButton);

            parent.Add(overlay);
        }

        public bool IsOpen
        {
            get { return isOpen; }
        }

        public void ShowSave(string initialDirectory, Action<string> onPathSelected)
        {
            Show(BrowserMode.Save, initialDirectory, onPathSelected);
        }

        public void ShowOpen(string initialDirectory, Action<string> onPathSelected)
        {
            Show(BrowserMode.Open, initialDirectory, onPathSelected);
        }

        public bool Close()
        {
            if (!IsOpen)
            {
                return false;
            }

            overlay.style.display = DisplayStyle.None;
            isOpen = false;
            pathSelected = null;
            selectedFilePath = null;
            HideOverwriteConfirmation();
            ClearError();
            return true;
        }

        private void Show(BrowserMode requestedMode, string initialDirectory, Action<string> onPathSelected)
        {
            Debug.Assert(onPathSelected != null, "[SceneSetupFileBrowser] A selection callback is required.");
            if (onPathSelected == null)
            {
                throw new ArgumentNullException(nameof(onPathSelected));
            }

            mode = requestedMode;
            pathSelected = onPathSelected;
            selectedFilePath = null;
            defaultDirectory = GetUsableInitialDirectory(initialDirectory);
            currentDirectory = GetRememberedDirectory(defaultDirectory);
            HideOverwriteConfirmation();
            ClearError();

            if (mode == BrowserMode.Save)
            {
                titleLabel.text = "Save OceanViz3 Scene Setup";
                primaryButton.text = "Save";
                saveNameRow.style.display = DisplayStyle.Flex;
                saveNameField.SetValueWithoutNotify("scene_setup" + requiredExtension);
                primaryButton.SetEnabled(true);
            }
            else
            {
                titleLabel.text = "Load OceanViz3 Scene Setup";
                primaryButton.text = "Load";
                saveNameRow.style.display = DisplayStyle.None;
                saveNameField.SetValueWithoutNotify(string.Empty);
                primaryButton.SetEnabled(false);
            }

            overlay.style.display = DisplayStyle.Flex;
            isOpen = true;
            overlay.BringToFront();
            RefreshEntries();

            if (mode == BrowserMode.Save)
            {
                saveNameField.Focus();
                saveNameField.SelectAll();
            }
        }

        private string GetUsableInitialDirectory(string initialDirectory)
        {
            if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            {
                return Path.GetFullPath(initialDirectory);
            }

            if (Directory.Exists(Application.persistentDataPath))
            {
                return Path.GetFullPath(Application.persistentDataPath);
            }

            Debug.LogError("[SceneSetupFileBrowser] No usable initial directory is available.");
            throw new DirectoryNotFoundException("No usable initial directory is available.");
        }

        private static string GetRememberedDirectory(string fallbackDirectory)
        {
            string rememberedDirectory = PlayerPrefs.GetString(LastDirectoryPlayerPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(rememberedDirectory) && Directory.Exists(rememberedDirectory))
            {
                return Path.GetFullPath(rememberedDirectory);
            }

            return fallbackDirectory;
        }

        private void RefreshEntries()
        {
            entriesScrollView.Clear();
            emptyDirectoryLabel.text = "No folders or scene setup files here.";
            emptyDirectoryLabel.style.display = DisplayStyle.None;
            entriesScrollView.Add(emptyDirectoryLabel);
            selectedFilePath = null;
            HideOverwriteConfirmation();
            ClearError();

            if (string.IsNullOrEmpty(currentDirectory))
            {
                pathField.SetValueWithoutNotify(string.Empty);
                PopulateRoots();
                return;
            }

            pathField.SetValueWithoutNotify(currentDirectory);

            try
            {
                DirectoryInfo directoryInfo = new DirectoryInfo(currentDirectory);
                DirectoryInfo[] directories = directoryInfo.GetDirectories();
                Array.Sort(directories, CompareDirectories);

                FileInfo[] files = directoryInfo.GetFiles();
                Array.Sort(files, CompareFiles);

                int entryCount = 0;
                for (int i = 0; i < directories.Length; i++)
                {
                    AddDirectoryEntry(directories[i]);
                    entryCount++;
                }

                for (int i = 0; i < files.Length; i++)
                {
                    if (!files[i].Extension.Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddFileEntry(files[i]);
                    entryCount++;
                }

                if (entryCount == 0)
                {
                    emptyDirectoryLabel.style.display = DisplayStyle.Flex;
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                ShowDirectoryReadError(exception);
            }
            catch (IOException exception)
            {
                ShowDirectoryReadError(exception);
            }
            catch (SecurityException exception)
            {
                ShowDirectoryReadError(exception);
            }
        }

        private static int CompareDirectories(DirectoryInfo left, DirectoryInfo right)
        {
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareFiles(FileInfo left, FileInfo right)
        {
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        private void PopulateRoots()
        {
            int rootCount = 0;
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo drive = drives[i];
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    AddRootEntry(drive.RootDirectory.FullName);
                    rootCount++;
                }
            }
            catch (IOException exception)
            {
                ShowError("Available roots could not be read.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ShowError("Available roots could not be read.", exception);
            }

            if (rootCount == 0)
            {
                string filesystemRoot = Path.GetPathRoot(Application.persistentDataPath);
                if (!string.IsNullOrEmpty(filesystemRoot) && Directory.Exists(filesystemRoot))
                {
                    AddRootEntry(filesystemRoot);
                    rootCount++;
                }
            }

            if (rootCount == 0)
            {
                emptyDirectoryLabel.text = "No accessible filesystem roots were found.";
                emptyDirectoryLabel.style.display = DisplayStyle.Flex;
            }
            else
            {
                emptyDirectoryLabel.text = "No folders or scene setup files here.";
            }
        }

        private void AddDirectoryEntry(DirectoryInfo directory)
        {
            Button entry = CreateEntryButton("Folder   " + directory.Name);
            string path = directory.FullName;
            entry.clicked += delegate { NavigateTo(path); };
            entriesScrollView.Add(entry);
        }

        private void AddRootEntry(string rootPath)
        {
            Button entry = CreateEntryButton("Root     " + rootPath);
            entry.clicked += delegate { NavigateTo(rootPath); };
            entriesScrollView.Add(entry);
        }

        private void AddFileEntry(FileInfo file)
        {
            Button entry = CreateEntryButton("Scene    " + file.Name);
            string path = file.FullName;
            entry.RegisterCallback<ClickEvent>(evt => OnFileEntryClicked(entry, path, evt.clickCount));
            entriesScrollView.Add(entry);
        }

        private Button CreateEntryButton(string text)
        {
            Button entry = new Button();
            entry.text = text;
            entry.style.height = 27.0f;
            entry.style.flexShrink = 0.0f;
            entry.style.unityTextAlign = TextAnchor.MiddleLeft;
            entry.style.marginTop = 1.0f;
            entry.style.marginRight = 2.0f;
            entry.style.marginBottom = 1.0f;
            entry.style.marginLeft = 2.0f;
            return entry;
        }

        private void OnFileEntryClicked(Button entry, string path, int clickCount)
        {
            selectedFilePath = path;
            ClearFileEntrySelection();
            entry.style.backgroundColor = new Color(0.20f, 0.38f, 0.55f, 1.0f);

            if (mode == BrowserMode.Save)
            {
                saveNameField.value = Path.GetFileName(path);
            }
            else
            {
                primaryButton.SetEnabled(true);
                if (clickCount >= 2)
                {
                    Complete(path);
                }
            }
        }

        private void ClearFileEntrySelection()
        {
            foreach (VisualElement child in entriesScrollView.Children())
            {
                Button button = child as Button;
                if (button != null)
                {
                    button.style.backgroundColor = StyleKeyword.Null;
                }
            }
        }

        private void NavigateToTypedPath()
        {
            NavigateTo(pathField.value);
        }

        private void OnPathFieldKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                NavigateToTypedPath();
                evt.StopPropagation();
            }
        }

        private void NavigateTo(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                ShowUserError("Enter a folder path first.");
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(requestedPath.Trim());
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                ShowError("That folder path is not valid.", exception);
                return;
            }

            if (!Directory.Exists(fullPath))
            {
                ShowUserError("That folder does not exist: " + fullPath);
                return;
            }

            currentDirectory = fullPath;
            RefreshEntries();
        }

        private void NavigateUp()
        {
            if (string.IsNullOrEmpty(currentDirectory))
            {
                return;
            }

            DirectoryInfo parent = Directory.GetParent(currentDirectory);
            if (parent == null)
            {
                ShowRoots();
                return;
            }

            NavigateTo(parent.FullName);
        }

        private void NavigateHome()
        {
            NavigateTo(defaultDirectory);
        }

        private void ShowRoots()
        {
            currentDirectory = null;
            RefreshEntries();
        }

        private void OnSaveNameChanged(ChangeEvent<string> evt)
        {
            selectedFilePath = null;
            HideOverwriteConfirmation();
            ClearError();
        }

        private void OnSaveNameKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                ConfirmSelection();
                evt.StopPropagation();
            }
        }

        private void ConfirmSelection()
        {
            if (mode == BrowserMode.Open)
            {
                if (string.IsNullOrEmpty(selectedFilePath))
                {
                    ShowUserError("Select a scene setup file first.");
                    return;
                }

                Complete(selectedFilePath);
                return;
            }

            ConfirmSaveSelection();
        }

        private void ConfirmSaveSelection()
        {
            if (string.IsNullOrEmpty(currentDirectory))
            {
                ShowUserError("Open a folder before saving.");
                return;
            }

            string fileName = saveNameField.value.Trim();
            if (string.IsNullOrEmpty(fileName))
            {
                ShowUserError("Enter a file name first.");
                return;
            }

            if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            {
                ShowUserError("Enter a file name without a folder path.");
                return;
            }

            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ShowUserError("The file name contains characters that are not allowed.");
                return;
            }

            if (!fileName.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                fileName += requiredExtension;
            }

            string savePath = Path.Combine(currentDirectory, fileName);
            if (File.Exists(savePath))
            {
                selectedFilePath = savePath;
                overwriteLabel.text = "A file named '" + fileName + "' already exists.";
                overwriteRow.style.display = DisplayStyle.Flex;
                return;
            }

            Complete(savePath);
        }

        private void ConfirmOverwrite()
        {
            Debug.Assert(!string.IsNullOrEmpty(selectedFilePath), "[SceneSetupFileBrowser] Overwrite confirmation requires a selected path.");
            if (string.IsNullOrEmpty(selectedFilePath))
            {
                throw new InvalidOperationException("Overwrite confirmation requires a selected path.");
            }

            Complete(selectedFilePath);
        }

        private void CancelOverwrite()
        {
            selectedFilePath = null;
            HideOverwriteConfirmation();
            saveNameField.Focus();
            saveNameField.SelectAll();
        }

        private void HideOverwriteConfirmation()
        {
            overwriteRow.style.display = DisplayStyle.None;
            overwriteLabel.text = string.Empty;
        }

        private void Complete(string path)
        {
            Debug.Assert(!string.IsNullOrEmpty(path), "[SceneSetupFileBrowser] Cannot complete with an empty path.");
            Debug.Assert(pathSelected != null, "[SceneSetupFileBrowser] Selection callback is missing.");
            if (string.IsNullOrEmpty(path) || pathSelected == null)
            {
                throw new InvalidOperationException("The file browser cannot complete without a path and callback.");
            }

            RememberCurrentDirectory();
            Action<string> callback = pathSelected;
            try
            {
                callback(path);
            }
            catch (UnauthorizedAccessException exception)
            {
                ShowFileOperationError(exception);
                return;
            }
            catch (IOException exception)
            {
                ShowFileOperationError(exception);
                return;
            }
            catch (SecurityException exception)
            {
                ShowFileOperationError(exception);
                return;
            }

            Close();
        }

        private void RememberCurrentDirectory()
        {
            if (string.IsNullOrEmpty(currentDirectory))
            {
                return;
            }

            PlayerPrefs.SetString(LastDirectoryPlayerPrefsKey, currentDirectory);
            PlayerPrefs.Save();
        }

        private void CloseWithoutSelection()
        {
            Close();
        }

        private void ShowDirectoryReadError(Exception exception)
        {
            ShowError("This folder could not be read. Choose another folder.", exception);
            emptyDirectoryLabel.style.display = DisplayStyle.Flex;
            emptyDirectoryLabel.text = "This folder is not accessible.";
        }

        private void ShowFileOperationError(Exception exception)
        {
            string operation = "saved";
            if (mode == BrowserMode.Open)
            {
                operation = "loaded";
            }

            ShowError("The scene setup file could not be " + operation + ".", exception);
        }

        private void ShowUserError(string message)
        {
            Debug.LogWarning("[SceneSetupFileBrowser] " + message);
            errorLabel.text = message;
            errorLabel.style.display = DisplayStyle.Flex;
        }

        private void ShowError(string message, Exception exception)
        {
            Debug.LogWarning("[SceneSetupFileBrowser] " + message + " " + exception.Message);
            errorLabel.text = message;
            errorLabel.style.display = DisplayStyle.Flex;
        }

        private void ClearError()
        {
            errorLabel.text = string.Empty;
            errorLabel.style.display = DisplayStyle.None;
        }

        private static VisualElement CreateHorizontalRow()
        {
            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexShrink = 0.0f;
            return row;
        }

        private static void SetCompactButtonWidth(Button button, float width)
        {
            button.style.width = width;
            button.style.height = 26.0f;
            button.style.flexShrink = 0.0f;
        }

        private static void SetFlexibleTextField(TextField textField)
        {
            textField.style.flexGrow = 1.0f;
            textField.style.flexShrink = 1.0f;
            textField.style.flexBasis = 0.0f;
            textField.style.minWidth = 0.0f;
        }
    }
}
