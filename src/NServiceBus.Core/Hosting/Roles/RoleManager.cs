﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Reflection;
using NServiceBus.Hosting.Helpers;
using NServiceBus.Logging;
using NServiceBus.Unicast.Config;
using NServiceBus.Utils.Reflection;

namespace NServiceBus.Hosting.Roles
{
    /// <summary>
    /// Handles the different roles that are registered
    /// </summary>
    public class RoleManager
    {
        private readonly IDictionary<Type, Type> availableRoles;
        private static readonly ILog Logger = LogManager.GetLogger(typeof(RoleManager));

        /// <summary>
        /// Creates the manager with the list of assemblies to scan for roles
        /// </summary>
        /// <param name="assembliesToScan"></param>
        public RoleManager(IEnumerable<Assembly> assembliesToScan)
        {
            availableRoles = assembliesToScan.AllTypes()
                .Select(t => new { Role = t.GetGenericallyContainedType(typeof(IConfigureRole<>), typeof(IRole)), Configurer = t })
                .Where(x => x.Role != null)
                .ToDictionary(key => key.Role, value => value.Configurer);
        }

        /// <summary>
        /// Checks if the specifier contains a given role and uses it to configure the UnicastBus appropriately.
        /// </summary>
        /// <param name="specifier"></param>
        public void ConfigureBusForEndpoint(IConfigureThisEndpoint specifier)
        {
            ConfigUnicastBus unicastBusConfig = null;

            foreach (var role in availableRoles)
            {
                var roleType = role.Key;
                bool handlesRole;


                if (roleType.IsGenericType)
                {
                    handlesRole =
                        specifier.GetType()
                                 .GetInterfaces()
                                 .Any(
                                     x =>
                                     x.IsGenericType &&
                                     x.GetGenericTypeDefinition() == roleType.GetGenericTypeDefinition());
                }
                else
                {
                    handlesRole = roleType.IsInstanceOfType(specifier);
                }

                if (!handlesRole)
                    continue;


                //apply role
                var roleConfigurer = Activator.CreateInstance(role.Value) as IConfigureRole;

                var config = roleConfigurer.ConfigureRole(specifier);

                if (config != null)
                {
                    if (unicastBusConfig != null)
                        throw new InvalidOperationException("Only one role can configure the unicastbus");

                    unicastBusConfig = config;
                }

                Logger.Info("Role " + roleType + " configured");
                foreach (var markerProfile in GetMarkerRoles(specifier.GetType(), roleType))
                    Logger.Info("Role " + markerProfile + " is marked.");
            }
        }

        private IEnumerable<string> GetMarkerRoles(Type configuredEndpoint, Type roleType)
        {
            return (from markerProfile in configuredEndpoint.GetInterfaces()
                    where markerProfile != roleType
                    where (markerProfile != typeof(IRole)) && (markerProfile.GetInterface(typeof(IRole).ToString()) != null)
                    select markerProfile.ToString()).ToList();
        }
    }


}