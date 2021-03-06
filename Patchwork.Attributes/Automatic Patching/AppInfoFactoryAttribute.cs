﻿using System;

namespace Patchwork.Attributes {
	/// <summary>
	/// You must decorate the <see cref="AppInfoFactory"/> class for your app assembly with this attribute so that Patchwork will find it.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, Inherited = false)]
	public class AppInfoFactoryAttribute : Attribute {
        
	}
}