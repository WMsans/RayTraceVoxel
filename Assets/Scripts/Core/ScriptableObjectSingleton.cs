using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public abstract class ScriptableObjectSingleton<T> : ScriptableObject where T : ScriptableObjectSingleton<T>
{
    private static T instance;

    public static T Instance
    {
        get
        {
            if (instance == null)
            {
                var assets = Resources.LoadAll<T>("");
                if (assets == null || assets.Length < 1)
                {
                    throw new System.Exception("Could not find any singleton scriptable object instance in the resources");
                }
                else if (assets.Length > 1)
                {
                    Debug.LogWarning("Multiple instances of the singleton scriptable object found in the resources.");
                }

                instance = assets[0];
            }

            return instance;
        }
    }
}
