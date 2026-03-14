using System;
using Godot;

public partial class MeasureModels : SceneTree
{
    public override void _Initialize()
    {
        string[] models = new[] { "atmmachine", "firebarrel", "foodstall", "marketstall", "toiletunit", "winestall" };
        
        foreach (var model in models)
        {
            try
            {
                var scene = GD.Load<PackedScene>($"res://Assets/{model}.glb");
                var node = scene.Instantiate() as Node3D;
                float minY = float.MaxValue;
                
                FindLowestYRec(node, Transform3D.Identity, ref minY);
                
                GD.Print($"MODEL: {model} | Lowest Y: {minY}");
            }
            catch (Exception) {}
        }
        
        Quit();
    }

    private void FindLowestYRec(Node n, Transform3D parentTransform, ref float minY)
    {
        Transform3D currentTransform = parentTransform;
        if (n is Node3D n3d)
        {
            currentTransform = parentTransform * n3d.Transform;
        }
        
        if (n is MeshInstance3D mi)
        {
            var aabb = mi.GetAabb();
            for (int i=0; i<8; i++)
            {
                Vector3 corner = currentTransform * aabb.GetEndpoint(i);
                if (corner.Y < minY) minY = corner.Y;
            }
        }
        
        foreach (var child in n.GetChildren())
        {
            FindLowestYRec(child, currentTransform, ref minY);
        }
    }
}
