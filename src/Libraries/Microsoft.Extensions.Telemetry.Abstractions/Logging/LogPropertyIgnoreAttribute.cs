﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Logging;

/// <summary>
/// Indicates that a tag should not be logged.
/// </summary>
/// <seealso cref="LoggerMessageAttribute"/>.
[AttributeUsage(AttributeTargets.Property)]
public sealed class LogPropertyIgnoreAttribute : Attribute
{
}
