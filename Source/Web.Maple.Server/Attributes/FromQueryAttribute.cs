
using System;

namespace Meadow.Foundation.Web.Maple
{
    /// <summary>
    /// Specifies that a parameter or property should be bound using the request query string.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public class FromQueryAttribute : Attribute
    {
        public string? Name { get; set; }
    }
}