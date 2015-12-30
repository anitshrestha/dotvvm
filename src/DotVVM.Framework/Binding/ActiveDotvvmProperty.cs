﻿using DotVVM.Framework.Controls;
using DotVVM.Framework.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotVVM.Framework.Binding
{
    public abstract class ActiveDotvvmProperty: DotvvmProperty
    {
        public abstract void AddAttributesToRender(IHtmlWriter writer, RenderContext context, object value, DotvvmControl control);


        public static ActiveDotvvmProperty RegisterCommandToAttribute<TDeclaringType>(string name, string attributeName)
        {
            return DelegateActionProperty<ICommandBinding>.Register<TDeclaringType>(name, (writer, context, binding, control) =>
            {
                var script = KnockoutHelper.GenerateClientPostBackScript(name, binding, context, control);
                writer.AddAttribute(attributeName, script);
            });
        }
    }
}
