using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace SolanaWalletService.Configuration;

internal sealed class InternalControllerFeatureProvider : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo)
    {
        if (typeInfo.IsPublic || typeInfo.IsNestedPublic)
            return false;

        if (!typeInfo.IsClass || typeInfo.IsAbstract || typeInfo.ContainsGenericParameters)
            return false;

        if (typeInfo.IsDefined(typeof(NonControllerAttribute), inherit: true))
            return false;

        return typeInfo.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            || typeInfo.IsDefined(typeof(ControllerAttribute), inherit: true);
    }
}
