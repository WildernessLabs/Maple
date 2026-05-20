using System;
using System.Collections.Generic;
using System.Linq;
using Meadow.Logging;

namespace Meadow.Foundation.Web.Maple;

public class AppDomainAssemblyRequestHandlerResolver : IRequestHandlerResolver
{
    public AppDomainAssemblyRequestHandlerResolver(Logger? logger)
    {
        Logger = logger;
    }

    readonly Logger? Logger;
    
    public Type[] Resolve()
    {
        var resolved = new List<Type>();
        
        // Get classes that implement IRequestHandler
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        // loop through each assembly in the app and all the classes in it
        foreach (var assembly in assemblies)
        {
            var types = assembly.GetTypes();
            foreach (var t in types)
            {
                // if it inherits `IRequestHandler`, add it to the list
                if (t.BaseType != null)
                {
                    if (t.BaseType.GetInterfaces().Contains(typeof(IRequestHandler)))
                    {
                        resolved.Add(t);
                    }
                }
            }
        }

        return resolved.ToArray();
    }
}