using GodotMcpPro.Commands;

namespace GodotMcpPro.Server;

public static class CommandModuleFactory
{
    public static IEnumerable<BaseCommand> CreateAll()
    {
        yield return new ProjectCommands();
        yield return new SceneCommands();
        yield return new NodeCommands();
        yield return new ScriptCommands();

        yield return new EditorCommands();
        yield return new InputCommands();
        yield return new InputMapCommands();
        yield return new ResourceCommands();
        yield return new ShaderCommands();

        yield return new RuntimeCommands();
        yield return new TestCommands();
        yield return new ProfilingCommands();

        yield return new AnimationCommands();
        yield return new AnimationTreeCommands();
        yield return new TilemapCommands();
        yield return new ThemeCommands();
        yield return new BatchCommands();
        yield return new AnalysisCommands();

        yield return new Scene3DCommands();
        yield return new ParticleCommands();
        yield return new PhysicsCommands();
        yield return new NavigationCommands();
        yield return new AudioCommands();
        yield return new ExportCommands();
        yield return new AndroidCommands();
        yield return new EditorExtensionCommands();
        yield return new TranslationCommands();
        yield return new ImportCommands();
        yield return new JointCommands();
        yield return new CurveCommands();
        yield return new SkeletonCommands();
        yield return new DiffCommands();
        yield return new UidCommands();
        yield return new MiscCommands();
        yield return new VisualShaderCommands();
    }
}
