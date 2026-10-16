# Texture Inspector regression checks

Run these checks in a disposable **3D** Unity project, with Unity UI (`com.unity.ugui`) installed. The fixtures must start with Unity's Default texture import settings.

1. Copy the repository's `Assets/PSDLayoutTool2` directory into the test project's `Assets` directory.
2. Copy `Tests/Editor/PsdInspectorRegressionTests.cs` into the test project's `Assets/Editor` directory.
3. Close the test project, then run Unity from PowerShell:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe' `
  -batchmode -nographics -quit `
  -projectPath 'C:\path\to\disposable-project' `
  -executeMethod PsdLayoutTool2.Tests.PsdInspectorRegressionTests.Run `
  -logFile 'C:\path\to\regression.log'
```

Use the installed editor path for other Unity versions. A successful run logs `PSD_INSPECTOR_REGRESSION_PASS`; an assertion failure causes the batch invocation to fail. Tests create their own temporary PNG, JPG and valid RGB PSD assets, and remove them afterward. No Unity Test Framework package is required.

The checks use Unity's actual selection/editor tracker, rather than manually constructing an importer editor. They cover:

- Native texture Inspector selection and initialized Apply/Revert state for PNG, JPG, `.psd` and uppercase `.PSD`.
- Pending Texture Type edits remaining unapplied on disk, and Revert restoring Default.
- Apply saving Sprite settings, generating a Sprite after reimport, and switching back to Default.
- Multiple ordinary textures, mixed PSD/PNG selection, and multiple PSD files.
- An unrelated text asset retaining its own Inspector.

Reflection in the test reads Unity's protected `needsApplyRevert` property to verify the condition that controls button visibility. Production plugin code does not use reflection or access Unity's private importer state.

## Interactive checks

Batch checks do not verify visual layout or click the GUI buttons. Use `Tools > PSD Layout Tool 2 > Prepare Inspector Test Assets` to generate fixtures for manual checks. In the test project, also check:

1. Select a PNG/JPG and change Texture Type between Default and Sprite. Apply and Revert remain visible, and Sprite Editor is available when the corresponding Unity package is installed.
2. Select a single PSD. The plugin controls appear once, above the native settings. Language switching, options and the existing export/layout/prefab actions remain available.
3. Multi-select images, lock a second Inspector and switch selections. Ordinary/mixed selections must not show PSD action controls or lose their native settings.
4. Recompile a script or reopen the project. The PSD controls should still appear once, without Console errors.
