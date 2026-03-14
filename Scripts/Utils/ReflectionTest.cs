using System;
using System.Reflection;
using Godot;

public partial class ReflectionTest : SceneTree
{
    public override void _Initialize()
    {
        var mat = new StandardMaterial3D();
        var props = mat.GetType().GetProperties();
        foreach (var p in props) {
            Console.WriteLine(p.Name);
        }
        Quit();
    }
}
