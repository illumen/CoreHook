﻿using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CoreHook.CoreLoad.Data;

namespace CoreHook.CoreLoad
{
    /*
     * Dependencies for CoreLoad:
     
     * Newtonsoft.Json.dll
     * Microsoft.Extensions.DependencyModel.dll
     * Microsoft.DotNet.PlatformAbstractions.dll
    */

    public class Loader
    {
        /// <summary>
        /// The interface implemented by each plugin that we initialize.
        /// </summary>
        private const string EntryPointInterface = "CoreHook.IEntryPoint";
        /// <summary>
        /// The name of the first method called in each plugin after initializing the class.
        /// </summary>
        private const string EntryPointMethodName = "Run";

        /// <summary>
        /// Initialize the plugin dependencies and execute its entry point.
        /// </summary>
        /// <param name="remoteParameters">Parameters containing the plugin to load
        /// and the parameters to pass to it's entry point.</param>
        /// <returns></returns>
        public static int Load(IntPtr remoteParameters)
        {
            try
            {
                if (remoteParameters == IntPtr.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(remoteParameters), 
                        "Remote arguments address was zero");
                }

                // Extract the plugin initialization information
                // from the remote host loader arguments.
                var remoteInfoFormatter = new UserDataBinaryFormatter();

                var pluginConfig =
                    PluginConfiguration<RemoteEntryInfo, ManagedRemoteInfo>.LoadData(
                        remoteParameters, remoteInfoFormatter
                    );

                IDependencyResolver resolver = CreateDependencyResolver(
                    pluginConfig.RemoteInfo.UserLibrary);
                
                // Construct the parameter array passed to the plugin initialization function.
                var pluginParameters = new object[1 + pluginConfig.RemoteInfo.UserParams.Length];

                pluginParameters[0] = pluginConfig.UnmanagedInfo;
                for (var i = 0; i < pluginConfig.RemoteInfo.UserParams.Length; ++i)
                {
                    pluginParameters[i + 1] = pluginConfig.RemoteInfo.UserParams[i];
                }
              
                DeserializeParameters(pluginParameters, remoteInfoFormatter);

                // Execute the plugin library's entry point and pass in the user arguments.
                pluginConfig.State = LoadPlugin(
                    resolver.Assembly,
                    pluginParameters,
                    pluginConfig.RemoteInfo.ChannelName);

                return (int)pluginConfig.State;
            }
            catch(ArgumentOutOfRangeException outOfRangeEx)
            {
                Log(outOfRangeEx.ToString());
                throw;
            }
            catch (Exception exception)
            {
                Log(exception.ToString());
            }
            return (int)PluginInitializationState.Failed;
        }

        /// <summary>
        /// Extract serialized parameters from a stream into a parameter list.
        /// </summary>
        /// <param name="paramArray">The list to store the extracted parameters to.</param>
        /// <param name="formatter">Extracts serialized objects into their original type.</param>
        private static void DeserializeParameters(object[] paramArray, IUserDataFormatter formatter)
        {
            for (int i = 1; i < paramArray.Length; ++i)
            {
                using (Stream ms = new MemoryStream((byte[])paramArray[i]))
                {
                    paramArray[i] = formatter.Deserialize<object>(ms);
                }
            }
        }

        /// <summary>
        /// Find the entry point of the plugin module, initialize it, and execute its Run method.
        /// </summary>
        /// <param name="assembly">The plugin assembly containing the entry point.</param>
        /// <param name="paramArray">The parameters passed to the plugin Run method.</param>
        /// <param name="helperPipeName">Pipe name used to notify the host about the state of the plugin initialization.</param>
        private static PluginInitializationState LoadPlugin(Assembly assembly, object[] paramArray, string helperPipeName)
        {
            Type entryPoint = FindEntryPoint(assembly);

            MethodInfo runMethod = FindMatchingMethod(entryPoint, EntryPointMethodName, paramArray);
            if(runMethod == null)
            {
                throw new MissingMethodException($"Failed to find the function 'Run' in {assembly.FullName}");
            }

            var instance = InitializeInstance(entryPoint, paramArray);
            if (instance == null)
            {
                throw new MissingMethodException($"Failed to find the constructor {entryPoint.Name} in {assembly.FullName}");
            }

            if (NotificationHelper.SendInjectionComplete(helperPipeName, Process.GetCurrentProcess().Id))
            {
                try
                {
                    // Execute the CoreHook plugin entry point
                    runMethod.Invoke(instance, BindingFlags.Public | BindingFlags.Instance | BindingFlags.ExactBinding |
                                               BindingFlags.InvokeMethod, null, paramArray, null);

                }
                catch
                {
                    
                }
                return PluginInitializationState.Initialized;
            }
            return PluginInitializationState.Failed;
        }

        /// <summary>
        /// Find the CoreHook entry point in the plugin module.
        /// </summary>
        /// <param name="assembly">The plugin module.</param>
        /// <returns>The CoreHook plugin entry point.</returns>
        private static Type FindEntryPoint(Assembly assembly)
        {
            Type[] exportedTypes = assembly.GetExportedTypes();
            foreach (var type in exportedTypes)
            {
                if (type.GetInterface(EntryPointInterface) != null)
                {
                    return type;
                }
            }
            return null;
        }

        /// <summary>
        /// Find a method from a type based on the name and parameters.
        /// </summary>
        /// <param name="objectType">The type containing the method to search for.</param>
        /// <param name="methodName">The name of the type's method.</param>
        /// <param name="paramArray">The parameters for the method.</param>
        /// <returns>Information about the matched method.</returns>
        private static MethodInfo FindMatchingMethod(Type objectType, string methodName, object[] paramArray)
        {
            var methods = objectType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var method in methods)
            {
                if (method.Name == methodName && (paramArray == null || MethodMatchesParameters(method, paramArray)))
                {
                    return method;
                }
            }
            return null;
        }

        /// <summary>
        /// Determine if a method's parameters match a list of user-defined parameters.
        /// </summary>
        /// <param name="method">The method to examine.</param>
        /// <param name="paramArray">The list of parameters that the method should have.</param>
        /// <returns>True of the list of expected parameters matches the method's parameters.</returns>
        private static bool MethodMatchesParameters(MethodBase method, object[] paramArray)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != paramArray.Length)
            {
                return false;
            }
            
            for (var i = 0; i < paramArray.Length; ++i)
            {
                if (!parameters[i].ParameterType.IsInstanceOfType(paramArray[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Invoke a class constructor with a list of parameters.
        /// </summary>
        /// <param name="objectType">The type who's constructor is called.</param>
        /// <param name="parameters">The parameters to pass to the class constructor.</param>
        /// <returns>The instance returned from calling the constructor.</returns>
        private static object InitializeInstance(Type objectType, object[] parameters)
        {
            var constructors = objectType.GetConstructors();
            foreach (var constructor in constructors)
            {
                if (MethodMatchesParameters(constructor, parameters))
                {
                    return constructor.Invoke(parameters);
                }
            }
            return null;
        }

        /// <summary>
        /// Initialize a class that resolves an assembly's dependencies.
        /// </summary>
        /// <param name="assemblyPath">The file path of the assembly.</param>
        /// <returns>An assembly dependency resolver.</returns>
        private static IDependencyResolver CreateDependencyResolver(string assemblyPath)
        {
            return new DependencyResolver(assemblyPath);
        }

        /// <summary>
        /// Log a message.
        /// </summary>
        /// <param name="message">The information to log.</param>
        private static void Log(string message)
        {
            Debug.WriteLine(message);
        }
    }
}
