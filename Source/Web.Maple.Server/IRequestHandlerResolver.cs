using System;

namespace Meadow.Foundation.Web.Maple;

public interface IRequestHandlerResolver
{
    Type[] Resolve();
}