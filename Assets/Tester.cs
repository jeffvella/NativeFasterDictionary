using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Experiment
{
    [BurstCompile]
    public struct TestJob : IJob
    {
        public NativeArray<StringKey> Keys;
        public NativeFasterDictionary<StringKey, SomeData> Map;

        public void Execute()
        {
            for (int i = 0; i < Map.Length; i++)
            {
                if (Map.TryFindIndex(Keys[i], out int findIndex))
                {
                    ref var v = ref Map.GetValue(findIndex);
                    v.Id = i;
                    v.Position = Vector3.up;
                }
            }
        }

    }

    [ExecuteInEditMode]
    public class Tester : MonoBehaviour
    {
        void Start()
        {

        }

        public void RunGo()
        {
            var words = "Lorem Ipsum is simply dummy text of the printing and typesetting industry. Lorem Ipsum has been the industry's standard dummy text ever since the 1500s, when an unknown printer took a galley of type and scrambled it to make a type specimen book.".Split(' ');
            var map = new NativeFasterDictionary<StringKey, SomeData>(100, Allocator.Persistent);
            var map2 = new NativeFasterDictionary<StringKey, SomeData>(100, Allocator.Persistent);
            var dict = new Dictionary<StringKey, SomeData>(100);
            var fdict = new FasterDictionary<StringKey, SomeData>(100);

            // I would test this against unity's NativeHashMap except they currently have no ability to set values by key :S

            try
            {
                for (int i = 0; i < words.Length; i++)
                {
                    var key = new StringKey(words[i]);
                    map[key] = new SomeData();
                    map2[key] = new SomeData();
                    dict[key] = new SomeData();
                    fdict[key] = new SomeData();
                }

                var randomKeys = new StringKey[map.Length];
                for (int i = 0; i < map.Length; i++)
                {
                    var idx = UnityEngine.Random.Range(0, map.Length - 1);
                    randomKeys[i] = new StringKey(words[idx]);
                }

                // Fair comparison assigning new value.
                NormalNativeDict(map, randomKeys);

                // Proper version using refs
                NormalNativeDictRef(map2, randomKeys);

                // Note the first time a job runs it's 10x slower.
                BurstJob(randomKeys, map);

                // Svelto's implementation.
                NormalFasterDict(fdict, randomKeys);

                // Stock standard C#
                NormalCSharpDict(dict, randomKeys);

                // Verify that the updates were correctly done
                for (int i = 0; i < words.Length; i++)
                {
                    var key = new StringKey(words[i]);

                    var id1 = map[key].Id;
                    var id2 = map2[key].Id;
                    var id3 = dict[key].Id;
                    var id4 = fdict[key].Id;
                    Debug.Assert(id1 == id2 && id1 == id3 && id1 == id4);

                    var p1 = map[key].Position;
                    var p2 = map2[key].Position;
                    var p3 = dict[key].Position;
                    var p4 = fdict[key].Position;
                    Debug.Assert(p1 == p2 && p1 == p3 && p1 == p4);
                }

                Debug.Log("-----------------------------------------");
            }
            catch (Exception e)
            {
                Debug.Log(e);
                throw;
            }
            finally
            {
                map.Dispose();
                map2.Dispose();
            }
        }

        private static void NormalNativeDict(NativeFasterDictionary<StringKey, SomeData> input, StringKey[] randomKeys)
        {
            var sw1 = new Stopwatch();
            sw1.Restart();
            var keys = randomKeys;
            var map = input;

            for (int i = 0; i < map.Length; i++)
            {
                var key = keys[i];
                if (map.TryGetValue(key, out var data))
                {
                    map[key] = new SomeData
                    {
                        Id = i,
                        Position = Vector3.up
                    };
                }
            }

            sw1.Stop();
            Debug.Log($"NativeDict (Value) Took {sw1.Elapsed.TotalMilliseconds:N4} ms");
        }


        private static void NormalNativeDictRef(NativeFasterDictionary<StringKey, SomeData> input, StringKey[] randomKeys)
        {
            var sw1 = new Stopwatch();
            sw1.Restart();
            var keys = randomKeys;
            var map = input;

            for (int i = 0; i < map.Length; i++)
            {
                if (map.TryFindIndex(keys[i], out var findIndex))
                {
                    ref var v = ref map.GetValue(findIndex);
                    v.Id = i;
                    v.Position = Vector3.up;
                }
            }

            sw1.Stop();
            Debug.Log($"NativeDict (Ref) Took {sw1.Elapsed.TotalMilliseconds:N4} ms");
        }


        private static void NormalCSharpDict(Dictionary<StringKey, SomeData> input, StringKey[] randomKeys)
        {
            var sw1 = new Stopwatch();
            sw1.Restart();
            var keys = randomKeys;
            var map = input;

            for (int i = 0; i < map.Count; i++)
            {
                var key = keys[i];
                if (map.TryGetValue(key, out var data))
                {
                    map[key] = new SomeData
                    {
                        Id = i,
                        Position = Vector3.up
                    };
                }
            }

            sw1.Stop();
            Debug.Log($"C# Dict Took {sw1.Elapsed.TotalMilliseconds:N4} ms");
        }


        private static void NormalFasterDict(FasterDictionary<StringKey, SomeData> input, StringKey[] randomKeys)
        {
            var sw1 = new Stopwatch();
            sw1.Restart();
            var keys = randomKeys;
            var map = input;

            for (int i = 0; i < map.Count; i++)
            {
                var key = keys[i];
                if (map.TryGetValue(key, out var data))
                {
                    map[key] = new SomeData
                    {
                        Id = i,
                        Position = Vector3.up
                    };
                }
            }

            sw1.Stop();
            Debug.Log($"FasterDict Took {sw1.Elapsed.TotalMilliseconds:N4} ms");
        }


        private static void BurstJob(StringKey[] randomKeys, NativeFasterDictionary<StringKey, SomeData> map)
        {
            var sw2 = new Stopwatch();
            
            using (var tmp = new NativeArray<StringKey>(randomKeys, Allocator.TempJob))
            {
                var job = new TestJob
                {
                    Keys = tmp,
                    Map = map
                };

                sw2.Restart();
                job.Run();
                sw2.Stop();
            }

            Debug.Log($"NativeDict (Burst Ref) Took {sw2.Elapsed.TotalMilliseconds:N4} ms");
        }
    }

    public struct SomeData
    {
        public int Id;
        public Vector3 Position;
        public Vector3 Velocity;
        public Quaternion Rotation;
    }






#if UNITY_EDITOR

    [CustomEditor(typeof(Tester))]
    [CanEditMultipleObjects]
    public class TestEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            foreach (var targ in targets.Cast<Tester>())
            {
                if (GUILayout.Button("Run"))
                {
                    targ.RunGo();
                }
                SceneView.RepaintAll();
            }
        }
    }

#endif



}
