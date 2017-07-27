﻿/* 
 * Copyright (c) 2014, Furore (info@furore.com) and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the BSD 3-Clause license
 * available at https://raw.githubusercontent.com/ewoutkramer/fhir-net-api/master/LICENSE
 */

using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Support;
using Hl7.Fhir.Utility;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;


namespace Hl7.Fhir.Serialization
{
    internal class ComplexTypeWriter
    {
        private IFhirWriter _writer;
        private ModelInspector _inspector;

        private readonly string[] summaryTextProperties = new[] { "id", "text", "meta", "fullurl", "resource", "entry", "search", "mode" };
        private readonly string[] summaryCountProperties = new[] { "resourcetype", "id", "type", "total" };

        internal enum SerializationMode
        {
            AllMembers,
            ValueElement,
            NonValueElements
        }

        public ComplexTypeWriter(IFhirWriter writer)
        {
            _writer = writer;
            _inspector = BaseFhirParser.Inspector;
        }

        internal void Serialize(ClassMapping mapping, object instance, Rest.SummaryType summary, SerializationMode mode = SerializationMode.AllMembers)
        {
            if (mapping == null) throw Error.ArgumentNull(nameof(mapping));

            _writer.WriteStartComplexContent();

            // Emit members that need xml attributes / first (to facilitate stream writer API)
            // attributes first (xml) and order maintained for the rest
            var propertiesToWrite = mapping.PropertyMappings
                .Where(property => property.SerializationHint == XmlSerializationHint.Attribute)
                .Concat(mapping.PropertyMappings.Where(property => property.SerializationHint != XmlSerializationHint.Attribute));

            if (summary == Rest.SummaryType.True)
            {
                propertiesToWrite = propertiesToWrite.Where(property => property.InSummary);
            }
            else if (summary == Rest.SummaryType.Text)
            {
                propertiesToWrite = propertiesToWrite.Where(property =>
                       summaryTextProperties.Contains(property.Name.ToLower())
                    || property.IsMandatoryElement
                    || isMetaTextOrIdElementInstance(instance));
            }
            else if (summary == Rest.SummaryType.Data)
            {
                propertiesToWrite = propertiesToWrite.Where(property => property.Name.ToLower() != "text");
            }
            else if (summary == Rest.SummaryType.Count)
            {
                propertiesToWrite = propertiesToWrite.Where(property => 
                   summaryCountProperties.Contains(property.Name.ToLower()) 
                || property.SerializationHint == XmlSerializationHint.Attribute);
            }

            foreach (var property in propertiesToWrite)
            {
                writeProperty(instance, summary, property, mode);
            }

            _writer.WriteEndComplexContent();
        }

        private void writeProperty(object instance, Rest.SummaryType summaryType, PropertyMapping property, SerializationMode mode)
        {
            // Check whether we are asked to just serialize the value element (Value members of primitive Fhir datatypes)
            // or only the other members (Extension, Id etc in primitive Fhir datatypes)
            // Default is all
            if (mode == SerializationMode.ValueElement && !property.RepresentsValueElement) return;
            if (mode == SerializationMode.NonValueElements && property.RepresentsValueElement) return;

            object value = property.GetValue(instance);

            if (value is IList && ((IList)value).Count == 0) return;

            bool isEnum = property.ElementType.IsEnum(),
                 isValueElement = property.RepresentsValueElement,
                 isEmptyPrimitive = instance is Primitive && string.IsNullOrEmpty(((Primitive)instance).ObjectValue as string);

            if (value == null && (!isEnum || !isValueElement || isEmptyPrimitive)) return;

            // Enumerated Primitive.Value of Code<T> will always serialize the ObjectValue, not the derived enumeration
            if (property.RepresentsValueElement && property.ElementType.IsEnum())
            {
                value = ((Primitive)instance).ObjectValue;
            }

            // For Choice properties, determine the actual name of the element
            // by appending its type to the base property name (i.e. deceasedBoolean, deceasedDate)
            string memberName = property.Choice == ChoiceType.DatatypeChoice
                    ? determineElementMemberName(property.Name, value.GetType())
                    : property.Name;

            _writer.WriteStartProperty(memberName);

            var writer = new DispatchingWriter(_writer);

            // Now, if our writer does not use dual properties for primitive values + rest (xml),
            // or this is a complex property without value element, serialize data normally

            if (_writer.HasValueElementSupport && serializedIntoTwoProperties(property, value))
            {
                writer.Serialize(property, value, summaryType, SerializationMode.ValueElement);
                _writer.WriteEndProperty();
                _writer.WriteStartProperty("_" + memberName);
                writer.Serialize(property, value, summaryType, SerializationMode.NonValueElements);
            }
            else
            {
                writer.Serialize(property, value, summaryType, SerializationMode.AllMembers);
            }

            _writer.WriteEndProperty();
        }

        // If we have a normal complex property, for which the type has a primitive value member...
        private bool serializedIntoTwoProperties(PropertyMapping prop, object instance)
        {
            if (instance is IList && ((IList)instance).Count > 0)
                instance = ((IList)instance)[0];

            if (prop.IsPrimitive || prop.Choice == ChoiceType.ResourceChoice)
                return false;

            return _inspector.ImportType(instance.GetType()).HasPrimitiveValueMember;
        }

        private static string upperCamel(string p)
        {
            if (p == null) return p;

            var c = p[0];

            return Char.ToUpperInvariant(c) + p.Remove(0, 1);
        }

        private string determineElementMemberName(string memberName, Type type)
        {
            var mapping = _inspector.ImportType(type);

            var suffix = mapping.Name;

            return memberName + upperCamel(suffix);
        }

        private bool isMetaTextOrIdElementInstance(object instance)
        {
            return (instance is Meta) || (instance is Narrative) || (instance is Id);
        }
    }
}
