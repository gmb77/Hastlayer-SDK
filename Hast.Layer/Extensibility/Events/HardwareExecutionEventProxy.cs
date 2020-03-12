﻿using Hast.Common.Interfaces;
using System;

namespace Hast.Layer.Extensibility.Events
{
    // This needs to be an app-level singleton so it's not recreated with shell restarts (otherwise enabling/disabling
    // features for example would cause the registered event handler to be lost. It can't have the same implementation
    // as IMemberInvocationEventHandler because than the letter's lifetime scope-level lifetime would take precedence.
    public interface IHardwareExecutionEventHandlerHolder : ISingletonDependency
    {
        void RegisterExecutedOnHardwareEventHandler(Action<ExecutedOnHardwareEventArgs> eventHandler);
        Action<ExecutedOnHardwareEventArgs> GetRegisteredEventHandler();
    }


    public class HardwareExecutionEventHandlerHolder : IHardwareExecutionEventHandlerHolder
    {
        private Action<ExecutedOnHardwareEventArgs> _eventHandler;


        public void RegisterExecutedOnHardwareEventHandler(Action<ExecutedOnHardwareEventArgs> eventHandler) =>
            // No need for locking since this will only be run once in a shell.
            _eventHandler = eventHandler;

        public Action<ExecutedOnHardwareEventArgs> GetRegisteredEventHandler() => _eventHandler;
    }
}
