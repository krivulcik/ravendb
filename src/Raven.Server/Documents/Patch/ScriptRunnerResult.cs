using System;
using Jurassic;
using Jurassic.Library;
using Sparrow.Json;

namespace Raven.Server.Documents.Patch
{
    public class ScriptRunnerResult : IDisposable
    {
        private readonly ScriptRunner.SingleRun _parent;
        private readonly object _instance;

        public ScriptRunnerResult(ScriptRunner.SingleRun parent,object instance)
        {
            _parent = parent;
            _instance = instance;
        }

        public object this[string name]
        {
            set => ((BlittableObjectInstance)_instance)[name] = value;
        }

        public ObjectInstance Get(string name)
        {
            if (_instance is ObjectInstance parent)
            {
                var o = parent[name] as ObjectInstance;
                if (o == null)
                {
                    parent[name] = o = parent.Engine.Object.Construct();
                }
                return o;
            }
            ThrowInvalidObject(name);
            return null; // never hit
        }

        private void ThrowInvalidObject(string name) => 
            throw new InvalidOperationException("Unable to get property '" + name + "' because the result is not an object but: " + _instance);

        public object Value => _instance;
        public bool IsNull => _instance == null || _instance == Null.Value || _instance == Undefined.Value;

        public T Translate<T>(JsonOperationContext context,
            BlittableJsonDocumentBuilder.UsageMode usageMode = BlittableJsonDocumentBuilder.UsageMode.None)
        {
            if (IsNull)
                return default(T);

            if (_instance is ArrayInstance)
                ThrowInvalidArrayResult();
            if (typeof(T) == typeof(BlittableJsonReaderObject))
            {
                if (_instance is ObjectInstance obj)
                    return (T)(object)JurrasicBlittableBridge.Translate(context, obj, usageMode);
                ThrowInvalidObject();
            }
            return (T)_instance;
        }

        private void ThrowInvalidObject() =>
            throw new InvalidOperationException("Cannot translate instance to object because it is: " + _instance);

        private static void ThrowInvalidArrayResult() =>
            throw new InvalidOperationException("Script cannot return an array.");

        public void Dispose()
        {
            _parent?.DisposeClonedDocuments();
        }
    }
}