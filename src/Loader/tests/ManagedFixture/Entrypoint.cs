namespace Doorstop;
public static class Entrypoint
{
    public static void Start() => File.WriteAllText("managed-entrypoint.ok", "real CoreCLR managed entrypoint reached");
}
