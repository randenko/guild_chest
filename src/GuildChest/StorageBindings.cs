using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GuildChest;

// Resolve optional mod internals at installation, then reuse the bindings on
// Unity's main thread. A missing required member disables the whole adapter.
internal static class StorageBindings
{
    internal static Type Type(Assembly assembly, string name) => assembly.GetType(name)
        ?? throw new TypeLoadException($"Missing storage adapter type {name}.");

    internal static MethodInfo Method(Type type, string name, Type? result = null, Type[]? arguments = null)
    {
        var method = AccessTools.Method(type, name, arguments)
            ?? throw new MissingMethodException(type.FullName, name);
        if (result != null && method.ReturnType != result)
            throw new InvalidOperationException($"Storage adapter method {type.FullName}.{name} has an unsupported return type.");
        return method;
    }

    internal static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name)
        ?? throw new MissingFieldException(type.FullName, name);

    internal static Func<T> Setting<T>(Type type, string name)
    {
        var field = Field(type, name);
        if (!field.IsStatic || !typeof(ConfigEntryBase).IsAssignableFrom(field.FieldType))
            throw new InvalidOperationException($"Storage adapter setting {type.FullName}.{name} is unsupported.");
        T Read() => (T)Convert.ChangeType(((ConfigEntryBase)field.GetValue(null)).BoxedValue, typeof(T));
        _ = Read(); // Validate the value before installing any hooks.
        return Read;
    }

    internal static MethodInfo NearbyQuery(Type type)
    {
        var method = Method(type, "GetNearbyContainers").MakeGenericMethod(typeof(Player));
        if (!method.IsStatic || !method.GetParameters().Select(parameter => parameter.ParameterType)
            .SequenceEqual(new[] { typeof(Player), typeof(float) }))
            throw new InvalidOperationException($"Storage adapter query {type.FullName}.GetNearbyContainers is unsupported.");
        return method;
    }

    internal static void Patch(Harmony harmony, Type adapter, params (MethodInfo Target, string Prefix)[] hooks)
    {
        var resolved = hooks.Select(hook => (hook.Target, Prefix: Method(adapter, hook.Prefix))).ToArray();
        try
        {
            foreach (var hook in resolved)
                harmony.Patch(hook.Target, prefix: new HarmonyMethod(hook.Prefix) { priority = Priority.First });
        }
        catch
        {
            foreach (var hook in resolved) harmony.Unpatch(hook.Target, hook.Prefix);
            throw;
        }
    }
}

internal sealed class ContainerBindings
{
    private static readonly Dictionary<Type, ContainerBindings> cache = new();
    private readonly FieldInfo? container;
    private readonly PropertyInfo? gameObject;
    private readonly MethodInfo? prefabName, itemCount;

    private ContainerBindings(Type type)
    {
        container = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(field => field.FieldType == typeof(Container));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        gameObject = type.GetProperty("gameObject", flags);
        prefabName = type.GetMethod("GetPrefabName", flags, null, System.Type.EmptyTypes, null);
        itemCount = type.GetMethod("ItemCount", flags, null, new[] { typeof(string) }, null);
    }

    private static ContainerBindings For(Type type)
    {
        if (!cache.TryGetValue(type, out var bindings)) cache.Add(type, bindings = new ContainerBindings(type));
        return bindings;
    }

    internal static void Validate(Type type, bool crafting)
    {
        var bindings = For(type);
        if (bindings.container == null) throw new MissingFieldException(type.FullName, "Container");
        if (!crafting)
        {
            if (bindings.gameObject?.PropertyType != typeof(GameObject)) throw new MissingMemberException(type.FullName, "gameObject");
            return;
        }
        if (bindings.prefabName?.ReturnType != typeof(string)) throw new MissingMethodException(type.FullName, "GetPrefabName");
        if (bindings.itemCount?.ReturnType != typeof(int)) throw new MissingMethodException(type.FullName, "ItemCount");
    }

    internal static Container? ContainerFor(object wrapper) => For(wrapper.GetType()).container?.GetValue(wrapper) as Container;
    internal static GameObject? ObjectFor(object wrapper) => For(wrapper.GetType()).gameObject?.GetValue(wrapper, null) as GameObject;
    internal static string PrefabFor(object wrapper) => (string)(For(wrapper.GetType()).prefabName
        ?? throw new MissingMethodException(wrapper.GetType().FullName, "GetPrefabName")).Invoke(wrapper, null);
    internal static int Count(object wrapper, string name) => (int)(For(wrapper.GetType()).itemCount
        ?? throw new MissingMethodException(wrapper.GetType().FullName, "ItemCount")).Invoke(wrapper, new object[] { name });
}
