using System.Reflection;
using GeometryEngine.Testing;

var filter = args.Length > 0 ? args[0] : null;

return TestRunner.Run(Assembly.GetExecutingAssembly(), filter);
