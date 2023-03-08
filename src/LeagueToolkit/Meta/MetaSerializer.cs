﻿using CommunityToolkit.Diagnostics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using LeagueToolkit.Helpers.Structures;
using LeagueToolkit.Meta.Attributes;
using System.Collections;
using System.Numerics;
using System.Reflection;

namespace LeagueToolkit.Meta
{
    public static class MetaSerializer
    {
        public static T Deserialize<T>(MetaEnvironment environment, BinTreeObject treeObject) where T : IMetaClass
        {
            // If object is already registered, return it
            if (environment.RegisteredObjects.TryGetValue(treeObject.PathHash, out IMetaClass existingObject))
            {
                if (existingObject is T concreteExistingObject)
                    return concreteExistingObject;
                else
                    ThrowHelper.ThrowInvalidOperationException(
                        $"Object: {treeObject.PathHash} is already registered under type: {nameof(T)}"
                    );
            }

            Type metaClassType = typeof(T);

            // Verify attribute
            if (
                metaClassType.GetCustomAttribute(typeof(MetaClassAttribute))
                is not MetaClassAttribute metaClassAttribute
            )
                throw new InvalidOperationException("The provided MetaClass does not have a MetaClass Attribute");
            if (metaClassAttribute.NameHash != treeObject.ClassHash)
                throw new InvalidOperationException("Meta Class name does not match class name of treeObject");

            // Create an instance of T and get its runtime type
            T metaClassObject = Activator.CreateInstance<T>();
            Type metaClassObjectType = metaClassObject.GetType();

            // Assign values to the object properties
            AssignMetaClassProperties(environment, metaClassObject, metaClassObjectType, treeObject.Properties);

            // Register the object in the environment for link resolving
            environment.RegisterObject(treeObject.PathHash, metaClassObject);

            return metaClassObject;
        }

        public static BinTreeObject Serialize<T>(MetaEnvironment environment, string path, T metaClass)
            where T : IMetaClass
        {
            return Serialize(environment, Fnv1a.HashLower(path), metaClass);
        }

        public static BinTreeObject Serialize<T>(MetaEnvironment environment, uint pathHash, T metaClass)
            where T : IMetaClass
        {
            Type metaClassType = metaClass.GetType();

            if (
                metaClassType.GetCustomAttribute(typeof(MetaClassAttribute))
                is not MetaClassAttribute metaClassAttribute
            )
                throw new InvalidOperationException("The provided MetaClass does not have a MetaClass Attribute");

            // Create Tree Properties for meta properties
            List<BinTreeProperty> properties = new();
            foreach (PropertyInfo propertyInfo in metaClassType.GetProperties())
            {
                BinTreeProperty treeProperty = ConvertPropertyToTreeProperty(environment, metaClass, propertyInfo);

                if (treeProperty is not null)
                    properties.Add(treeProperty);
            }

            return new(pathHash, metaClassAttribute.NameHash, properties);
        }

        // ------------ DESERIALIZATION ASSIGNMENT ------------ \\
        private static void AssignMetaClassProperties(
            MetaEnvironment environment,
            object metaClassObject,
            Type metaClassType,
            IReadOnlyDictionary<uint, BinTreeProperty> treeProperties
        )
        {
            PropertyInfo[] properties = metaClassType.GetProperties();

            foreach (PropertyInfo propertyInfo in properties)
            {
                // Ignore non-meta properties
                if (
                    propertyInfo.GetCustomAttribute(typeof(MetaPropertyAttribute))
                    is not MetaPropertyAttribute metaPropertyAttribute
                )
                    continue;

                // Find matching tree property and ignore missing properties
                if (treeProperties.TryGetValue(metaPropertyAttribute.NameHash, out BinTreeProperty treeProperty))
                    AssignMetaProperty(environment, metaClassObject, propertyInfo, treeProperty);
            }
        }

        private static void AssignMetaProperty(
            MetaEnvironment environment,
            object metaClassObject,
            PropertyInfo propertyInfo,
            BinTreeProperty treeProperty
        )
        {
            propertyInfo.SetValue(
                metaClassObject,
                DeserializeTreeProperty(environment, treeProperty, propertyInfo.PropertyType)
            );
        }

        // ------------ PROPERTY DESERIALIZATION ------------ \\
        private static object DeserializeTreeProperty(
            MetaEnvironment environment,
            BinTreeProperty treeProperty,
            Type propertyType = null
        )
        {
            BinPropertyType treePropertyType = treeProperty.Type;

            if (IsPrimitivePropertyType(treePropertyType))
            {
                return FetchPrimitivePropertyValue(treeProperty);
            }
            else if (
                treePropertyType == BinPropertyType.Container || treePropertyType == BinPropertyType.UnorderedContainer
            )
            {
                return DeserializeContainer(environment, propertyType, treeProperty as BinTreeContainer);
            }
            else if (treePropertyType == BinPropertyType.Struct)
            {
                return DeserializeStructure(environment, treeProperty as BinTreeStruct);
            }
            else if (treePropertyType == BinPropertyType.Embedded)
            {
                return DeserializeEmbedded(environment, treeProperty as BinTreeEmbedded);
            }
            else if (treePropertyType == BinPropertyType.Map)
            {
                return DeserializeMap(environment, propertyType, treeProperty as BinTreeMap);
            }
            else if (treePropertyType == BinPropertyType.Optional)
            {
                return DeserializeOptional(environment, propertyType, treeProperty as BinTreeOptional);
            }

            return null;
        }

        private static object DeserializeStructure(MetaEnvironment environment, BinTreeStruct structure)
        {
            Type metaClassType = environment.GetMetaClassTypeOrDefault(structure.ClassHash);
            if (metaClassType is null)
                return null; // Couldn't deserialize structure

            object metaClassObject = Activator.CreateInstance(metaClassType);

            AssignMetaClassProperties(environment, metaClassObject, metaClassObject.GetType(), structure.Properties);

            return metaClassObject;
        }

        private static object DeserializeEmbedded(MetaEnvironment environment, BinTreeEmbedded embedded)
        {
            Type metaClassType = environment.GetMetaClassTypeOrDefault(embedded.ClassHash);
            if (metaClassType is null)
                return null; // Couldn't deserialize structure

            Type embeddedWrapperType = typeof(MetaEmbedded<>).MakeGenericType(metaClassType);

            object metaClassObject = Activator.CreateInstance(metaClassType);
            AssignMetaClassProperties(environment, metaClassObject, metaClassObject.GetType(), embedded.Properties);

            object embeddedWrapperObject = Activator.CreateInstance(embeddedWrapperType, new[] { metaClassObject });
            return embeddedWrapperObject;
        }

        private static object DeserializeContainer(
            MetaEnvironment environment,
            Type propertyType,
            BinTreeContainer container
        )
        {
            object containerList = Activator.CreateInstance(propertyType);
            Type containerListType = containerList.GetType();
            MethodInfo addMethod = containerListType.GetMethod("Add");

            foreach (BinTreeProperty containerItem in container.Elements)
            {
                addMethod.Invoke(containerList, new[] { DeserializeTreeProperty(environment, containerItem) });
            }

            return containerList;
        }

        private static object DeserializeMap(MetaEnvironment environment, Type propertyType, BinTreeMap map)
        {
            // Invalid key type
            if (IsValidMapKey(map.KeyType) is false)
                return null;

            object mapDictionary = Activator.CreateInstance(propertyType);
            Type mapDictionaryType = mapDictionary.GetType();
            MethodInfo addMethod = mapDictionaryType.GetMethod("Add");

            foreach (var (key, value) in map)
            {
                // Key types can only be primitive so we can fetch their value easily
                object keyValue = FetchPrimitivePropertyValue(key);
                object valueValue = DeserializeTreeProperty(environment, value);

                addMethod.Invoke(mapDictionary, new[] { keyValue, valueValue });
            }

            return mapDictionary;
        }

        private static object DeserializeOptional(
            MetaEnvironment environment,
            Type propertyType,
            BinTreeOptional optional
        )
        {
            bool isSome = optional.Value is not null;
            object value = isSome ? DeserializeTreeProperty(environment, optional.Value) : GetTypeDefault(propertyType);
            object optionalObject = Activator.CreateInstance(propertyType, new[] { value, isSome });

            return optionalObject;
        }

        // ------------ SERIALIZATION ------------ \\
        private static BinTreeProperty ConvertPropertyToTreeProperty(
            MetaEnvironment environment,
            object metaClassObject,
            PropertyInfo propertyInfo
        )
        {
            if (
                propertyInfo.GetCustomAttribute(typeof(MetaPropertyAttribute))
                is not MetaPropertyAttribute metaPropertyAttribute
            )
                throw new InvalidOperationException("The specified property does not have a MetaProperty Attribute");

            object value = propertyInfo.GetValue(metaClassObject);

            return ConvertObjectToProperty(
                environment,
                metaPropertyAttribute.NameHash,
                value,
                propertyInfo.PropertyType
            );
        }

        private static BinTreeProperty ConvertObjectToProperty(
            MetaEnvironment environment,
            uint nameHash,
            object value,
            Type valueType
        )
        {
            // Handle primitives
            if (value is null)
                return null;
            else if (valueType == typeof(bool?))
                return new BinTreeBool(nameHash, (bool)value);
            else if (valueType == typeof(sbyte?))
                return new BinTreeI8(nameHash, (sbyte)value);
            else if (valueType == typeof(byte?))
                return new BinTreeU8(nameHash, (byte)value);
            else if (valueType == typeof(short?))
                return new BinTreeI16(nameHash, (short)value);
            else if (valueType == typeof(ushort?))
                return new BinTreeU16(nameHash, (ushort)value);
            else if (valueType == typeof(int?))
                return new BinTreeI32(nameHash, (int)value);
            else if (valueType == typeof(uint?))
                return new BinTreeU32(nameHash, (uint)value);
            else if (valueType == typeof(long?))
                return new BinTreeI64(nameHash, (long)value);
            else if (valueType == typeof(ulong?))
                return new BinTreeU64(nameHash, (ulong)value);
            else if (valueType == typeof(float?))
                return new BinTreeF32(nameHash, (float)value);
            else if (valueType == typeof(Vector2?))
                return new BinTreeVector2(nameHash, (Vector2)value);
            else if (valueType == typeof(Vector3?))
                return new BinTreeVector3(nameHash, (Vector3)value);
            else if (valueType == typeof(Vector4?))
                return new BinTreeVector4(nameHash, (Vector4)value);
            else if (valueType == typeof(Matrix4x4?))
                return new BinTreeMatrix44(nameHash, (Matrix4x4)value);
            else if (valueType == typeof(Color?))
                return new BinTreeColor(nameHash, (Color)value);
            else if (valueType == typeof(string))
                return new BinTreeString(nameHash, (string)value);
            else if (valueType == typeof(MetaHash?))
                return new BinTreeHash(nameHash, (MetaHash)value);
            else if (valueType == typeof(MetaWadEntryLink?))
                return new BinTreeWadChunkLink(nameHash, (MetaWadEntryLink)value);
            else if (valueType == typeof(MetaObjectLink?))
                return new BinTreeObjectLink(nameHash, (MetaObjectLink)value);
            else if (valueType == typeof(MetaBitBool?))
                return new BinTreeBitBool(nameHash, (MetaBitBool)value);
            else
            {
                // Handle complex types
                if (valueType.IsGenericType)
                {
                    Type genericTypeDefinition = valueType.GetGenericTypeDefinition();

                    if (genericTypeDefinition == typeof(Dictionary<,>))
                    {
                        return CreateMapProperty(
                            environment,
                            nameHash,
                            valueType.GenericTypeArguments[0],
                            valueType.GenericTypeArguments[1],
                            value as IDictionary
                        );
                    }
                    else if (genericTypeDefinition == typeof(MetaUnorderedContainer<>))
                    {
                        return CreateUnorderedContainerProperty(
                            environment,
                            nameHash,
                            valueType.GenericTypeArguments[0],
                            value as IEnumerable
                        );
                    }
                    else if (genericTypeDefinition == typeof(MetaContainer<>))
                    {
                        return CreateContainerProperty(
                            environment,
                            nameHash,
                            valueType.GenericTypeArguments[0],
                            value as IEnumerable
                        );
                    }
                    else if (genericTypeDefinition == typeof(MetaOptional<>))
                    {
                        return CreateOptionalProperty(
                            environment,
                            nameHash,
                            valueType.GenericTypeArguments[0],
                            value as IMetaOptional
                        );
                    }
                    else if (genericTypeDefinition == typeof(MetaEmbedded<>))
                    {
                        return CreateEmbeddedProperty(
                            environment,
                            nameHash,
                            valueType.GenericTypeArguments[0],
                            value as IMetaEmbedded
                        );
                    }
                    else
                        return null;
                }
                else
                {
                    // Check if we're dealing with a Structure type
                    if (valueType.IsValueType is false && valueType.GetInterface(nameof(IMetaClass)) is not null)
                    {
                        return CreateStructureProperty(environment, value, nameHash);
                    }
                    else
                        return null;
                }
            }
        }

        private static BinTreeStruct CreateStructureProperty(
            MetaEnvironment environment,
            object structureObject,
            uint nameHash
        )
        {
            Type structureType = structureObject.GetType();
            if (
                structureType.GetCustomAttribute(typeof(MetaClassAttribute))
                is not MetaClassAttribute metaClassAttribute
            )
                throw new InvalidOperationException("The specified property does not have a MetaClass Attribute");

            // Create properties
            List<BinTreeProperty> properties = new();
            foreach (PropertyInfo propertyInfo in structureType.GetProperties())
            {
                BinTreeProperty property = ConvertPropertyToTreeProperty(environment, structureObject, propertyInfo);
                if (property is not null)
                    properties.Add(property);
            }

            return new(nameHash, metaClassAttribute.NameHash, properties);
        }

        private static BinTreeMap CreateMapProperty(
            MetaEnvironment environment,
            uint nameHash,
            Type keyType,
            Type valueType,
            IDictionary map
        )
        {
            // Get key and value types
            BinPropertyType keyPropertyType = GetPropertyTypeFromType(keyType);
            BinPropertyType valuePropertyType = GetPropertyTypeFromType(valueType);

            // Create keys and values
            Dictionary<BinTreeProperty, BinTreeProperty> convertedMap = new();
            foreach (DictionaryEntry entry in map)
            {
                BinTreeProperty key = ConvertObjectToProperty(environment, 0, entry.Key, keyType);
                BinTreeProperty value = ConvertObjectToProperty(environment, 0, entry.Value, valueType);

                if (key is not null && value is not null)
                {
                    convertedMap.Add(key, value);
                }
            }

            return new(nameHash, keyPropertyType, valuePropertyType, convertedMap);
        }

        private static BinTreeUnorderedContainer CreateUnorderedContainerProperty(
            MetaEnvironment environment,
            uint nameHash,
            Type itemType,
            IEnumerable unorderedContainer
        )
        {
            BinPropertyType itemPropertyType = GetPropertyTypeFromType(itemType);

            // Create properties
            List<BinTreeProperty> properties = new();
            foreach (object item in unorderedContainer)
            {
                BinTreeProperty property = ConvertObjectToProperty(environment, 0, item, itemType);
                if (property is not null)
                    properties.Add(property);
            }

            return new(nameHash, itemPropertyType, properties);
        }

        private static BinTreeContainer CreateContainerProperty(
            MetaEnvironment environment,
            uint nameHash,
            Type itemType,
            IEnumerable container
        )
        {
            BinPropertyType itemPropertyType = GetPropertyTypeFromType(itemType);

            // Create properties
            List<BinTreeProperty> properties = new();
            foreach (object item in container)
            {
                BinTreeProperty property = ConvertObjectToProperty(environment, 0, item, itemType);
                if (property is not null)
                    properties.Add(property);
            }

            return new(nameHash, itemPropertyType, properties);
        }

        private static BinTreeOptional CreateOptionalProperty(
            MetaEnvironment environment,
            uint nameHash,
            Type valueType,
            IMetaOptional optional
        )
        {
            object value = optional.GetValue();
            BinTreeProperty optionalValue = ConvertObjectToProperty(environment, 0, value, valueType);

            return optionalValue switch
            {
                null => null,
                BinTreeProperty someValue => new(nameHash, someValue)
            };
        }

        private static BinTreeEmbedded CreateEmbeddedProperty(
            MetaEnvironment environment,
            uint nameHash,
            Type valueType,
            IMetaEmbedded embeddedObject
        )
        {
            if (valueType.GetCustomAttribute(typeof(MetaClassAttribute)) is not MetaClassAttribute metaClassAttribute)
                throw new InvalidOperationException("The specified property does not have a MetaClass Attribute");

            object embdeddedValue = embeddedObject.GetValue();

            // Create properties
            List<BinTreeProperty> properties = new();
            foreach (PropertyInfo propertyInfo in valueType.GetProperties())
            {
                BinTreeProperty property = ConvertPropertyToTreeProperty(environment, embdeddedValue, propertyInfo);

                if (property is not null)
                    properties.Add(property);
            }

            return new(nameHash, metaClassAttribute.NameHash, properties);
        }

        // ------------ HELPER METHODS ------------ \\
        private static bool IsPrimitivePropertyType(BinPropertyType propertyType)
        {
            return propertyType switch
            {
                BinPropertyType.None => true,
                BinPropertyType.Bool => true,
                BinPropertyType.I8 => true,
                BinPropertyType.U8 => true,
                BinPropertyType.I16 => true,
                BinPropertyType.U16 => true,
                BinPropertyType.I32 => true,
                BinPropertyType.U32 => true,
                BinPropertyType.I64 => true,
                BinPropertyType.U64 => true,
                BinPropertyType.F32 => true,
                BinPropertyType.Vector2 => true,
                BinPropertyType.Vector3 => true,
                BinPropertyType.Vector4 => true,
                BinPropertyType.Matrix44 => true,
                BinPropertyType.Color => true,
                BinPropertyType.String => true,
                BinPropertyType.Hash => true,
                BinPropertyType.WadChunkLink => true,
                BinPropertyType.Container => false,
                BinPropertyType.UnorderedContainer => false,
                BinPropertyType.Struct => false,
                BinPropertyType.Embedded => false,
                BinPropertyType.ObjectLink => true,
                BinPropertyType.Optional => false,
                BinPropertyType.Map => false,
                BinPropertyType.BitBool => true,
                _ => throw new ArgumentException("Invalid property type", nameof(propertyType))
            };
        }

        private static bool IsValidMapKey(BinPropertyType propertyType) =>
            propertyType switch
            {
                BinPropertyType.None => false,
                BinPropertyType.Bool => false,
                BinPropertyType.I8 => true,
                BinPropertyType.U8 => true,
                BinPropertyType.I16 => true,
                BinPropertyType.U16 => true,
                BinPropertyType.I32 => true,
                BinPropertyType.U32 => true,
                BinPropertyType.I64 => true,
                BinPropertyType.U64 => true,
                BinPropertyType.F32 => false,
                BinPropertyType.Vector2 => false,
                BinPropertyType.Vector3 => false,
                BinPropertyType.Vector4 => false,
                BinPropertyType.Matrix44 => false,
                BinPropertyType.Color => false,
                BinPropertyType.String => true,
                BinPropertyType.Hash => true,
                BinPropertyType.WadChunkLink => false,
                BinPropertyType.Container => false,
                BinPropertyType.UnorderedContainer => false,
                BinPropertyType.Struct => false,
                BinPropertyType.Embedded => false,
                BinPropertyType.ObjectLink => false,
                BinPropertyType.Optional => false,
                BinPropertyType.Map => false,
                BinPropertyType.BitBool => false,
                _ => throw new ArgumentException("Invalid property type", nameof(propertyType))
            };

        private static object FetchPrimitivePropertyValue(BinTreeProperty primitiveProperty)
        {
            return primitiveProperty switch
            {
                BinTreeNone _ => null,
                BinTreeBool property => property.Value,
                BinTreeI8 property => property.Value,
                BinTreeU8 property => property.Value,
                BinTreeI16 property => property.Value,
                BinTreeU16 property => property.Value,
                BinTreeI32 property => property.Value,
                BinTreeU32 property => property.Value,
                BinTreeI64 property => property.Value,
                BinTreeU64 property => property.Value,
                BinTreeF32 property => property.Value,
                BinTreeVector2 property => property.Value,
                BinTreeVector3 property => property.Value,
                BinTreeVector4 property => property.Value,
                BinTreeMatrix44 property => property.Value,
                BinTreeColor property => property.Value,
                BinTreeString property => property.Value,
                BinTreeHash property => new MetaHash(property.Value),
                BinTreeWadChunkLink property => new MetaWadEntryLink(property.Value),
                BinTreeObjectLink property => new MetaObjectLink(property.Value),
                BinTreeBitBool property => new MetaBitBool(property.Value),
                _ => null
            };
        }

        private static object GetTypeDefault(Type type)
        {
            return type.IsValueType switch
            {
                true => Activator.CreateInstance(type),
                false => null
            };
        }

        private static BinPropertyType GetPropertyTypeFromType(Type type)
        {
            // Primitive types
            if (type == typeof(bool))
                return BinPropertyType.Bool;
            else if (type == typeof(sbyte))
                return BinPropertyType.I8;
            else if (type == typeof(byte))
                return BinPropertyType.U8;
            else if (type == typeof(short))
                return BinPropertyType.I16;
            else if (type == typeof(ushort))
                return BinPropertyType.U16;
            else if (type == typeof(int))
                return BinPropertyType.I32;
            else if (type == typeof(uint))
                return BinPropertyType.U32;
            else if (type == typeof(long))
                return BinPropertyType.I64;
            else if (type == typeof(ulong))
                return BinPropertyType.U64;
            else if (type == typeof(float))
                return BinPropertyType.F32;
            else if (type == typeof(Vector2))
                return BinPropertyType.Vector2;
            else if (type == typeof(Vector3))
                return BinPropertyType.Vector3;
            else if (type == typeof(Vector4))
                return BinPropertyType.Vector4;
            else if (type == typeof(Matrix4x4))
                return BinPropertyType.Matrix44;
            else if (type == typeof(Color))
                return BinPropertyType.Color;
            else if (type == typeof(string))
                return BinPropertyType.String;
            else if (type == typeof(MetaHash))
                return BinPropertyType.Hash;
            else if (type == typeof(MetaWadEntryLink))
                return BinPropertyType.WadChunkLink;
            else if (type == typeof(MetaObjectLink))
                return BinPropertyType.ObjectLink;
            else if (type == typeof(MetaBitBool))
                return BinPropertyType.BitBool;
            else
            {
                if (type.IsGenericType)
                {
                    Type genericTypeDefinition = type.GetGenericTypeDefinition();

                    if (genericTypeDefinition == typeof(Dictionary<,>))
                        return BinPropertyType.Map;
                    else if (genericTypeDefinition == typeof(MetaUnorderedContainer<>))
                        return BinPropertyType.UnorderedContainer;
                    else if (genericTypeDefinition == typeof(MetaContainer<>))
                        return BinPropertyType.Container;
                    else if (genericTypeDefinition == typeof(MetaOptional<>))
                        return BinPropertyType.Optional;
                    else if (genericTypeDefinition == typeof(MetaEmbedded<>))
                        return BinPropertyType.Embedded;
                    else
                        throw new ArgumentException(nameof(type), "Failed to match with a valid property type");
                }
                else if (type.IsValueType is false && type.GetInterface(nameof(IMetaClass)) is not null)
                {
                    return BinPropertyType.Struct;
                }
                else
                {
                    throw new ArgumentException(nameof(type), "Failed to match with a valid property type");
                }
            }
        }
    }
}
