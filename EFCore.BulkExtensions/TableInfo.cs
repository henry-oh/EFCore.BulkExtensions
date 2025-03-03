using EFCore.BulkExtensions.SqlAdapters;
using EFCore.BulkExtensions.SQLAdapters.SQLServer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EFCore.BulkExtensions;

/// <summary>
/// Provides a list of information for EFCore.BulkExtensions that is used internally to know what to do with the data source received
/// </summary>
public class TableInfo
{
#pragma warning disable CS1591 // No XML comments required here.
    public string? Schema { get; set; }
    public string SchemaFormated => Schema != null ? $"[{Schema}]." : "";
    public string? TempSchema { get; set; }
    public string TempSchemaFormated => TempSchema != null ? $"[{TempSchema}]." : "";
    public string? TableName { get; set; }
    public string FullTableName => $"{SchemaFormated}[{TableName}]";
    public Dictionary<string, string> PrimaryKeysPropertyColumnNameDict { get; set; } = null!;
    public Dictionary<string, string> EntityPKPropertyColumnNameDict { get; set; } = null!;
    public bool HasSinglePrimaryKey { get; set; }
    public bool UpdateByPropertiesAreNullable { get; set; }

    protected string TempDBPrefix => BulkConfig.UseTempDB ? "#" : "";
    public string? TempTableSufix { get; set; }
    public string? TempTableName { get; set; }
    public string FullTempTableName => $"{TempSchemaFormated}[{TempDBPrefix}{TempTableName}]";
    public string FullTempOutputTableName => $"{SchemaFormated}[{TempDBPrefix}{TempTableName}Output]";

    public bool CreatedOutputTable => BulkConfig.SetOutputIdentity || BulkConfig.CalculateStats;

    public bool InsertToTempTable { get; set; }
    public string? IdentityColumnName { get; set; }
    public bool HasIdentity => IdentityColumnName != null;
    public bool HasOwnedTypes { get; set; }
    public bool HasAbstractList { get; set; }
    public bool ColumnNameContainsSquareBracket { get; set; }
    public bool LoadOnlyPKColumn { get; set; }
    public bool HasSpatialType { get; set; }
    public bool HasTemporalColumns { get; set; }
    public int NumberOfEntities { get; set; }

    public BulkConfig BulkConfig { get; set; } = null!;
    public Dictionary<string, string> OutputPropertyColumnNamesDict { get; set; } = new();
    public Dictionary<string, string> PropertyColumnNamesDict { get; set; } = new();
    public Dictionary<string, string> ColumnNamesTypesDict { get; set; } = new();
    public Dictionary<string, IProperty> ColumnToPropertyDictionary { get; set; } = new();
    public Dictionary<string, string> PropertyColumnNamesCompareDict { get; set; } = new();
    public Dictionary<string, string> PropertyColumnNamesUpdateDict { get; set; } = new();
    public Dictionary<string, FastProperty> FastPropertyDict { get; set; } = new();
    public Dictionary<string, INavigation> AllNavigationsDictionary { get; private set; } = null!;
    public Dictionary<string, INavigation> OwnedTypesDict { get; set; } = new();
    public HashSet<string> ShadowProperties { get; set; } = new HashSet<string>();
    public HashSet<string> DefaultValueProperties { get; set; } = new HashSet<string>();

    public Dictionary<string, string> ConvertiblePropertyColumnDict { get; set; } = new Dictionary<string, string>();
    public Dictionary<string, ValueConverter> ConvertibleColumnConverterDict { get; set; } = new Dictionary<string, ValueConverter>();
    public Dictionary<string, int> DateTime2PropertiesPrecisionLessThen7Dict { get; set; } = new Dictionary<string, int>();

    public static string TimeStampOutColumnType => "varbinary(8)";
    public string? TimeStampPropertyName { get; set; }
    public string? TimeStampColumnName { get; set; }

    protected IList<object>? EntitiesSortedReference { get; set; } // Operation Merge writes In Output table first Existing that were Updated then for new that were Inserted so this makes sure order is same in list when need to set Output

    public StoreObjectIdentifier ObjectIdentifier { get; set; }

    internal SqliteConnection? SqliteConnection { get; set; }
    internal SqliteTransaction? SqliteTransaction { get; set; }


    internal NpgsqlConnection? NpgsqlConnection { get; set; }
    internal NpgsqlTransaction? NpgsqlTransaction { get; set; }


#pragma warning restore CS1591 // No XML comments required here.

    /// <summary>
    /// Creates an instance of TableInfo
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="context"></param>
    /// <param name="type"></param>
    /// <param name="entities"></param>
    /// <param name="operationType"></param>
    /// <param name="bulkConfig"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static TableInfo CreateInstance<T>(DbContext context, Type? type, IList<T> entities, OperationType operationType, BulkConfig? bulkConfig)
    {
        var tableInfo = new TableInfo
        {
            NumberOfEntities = entities.Count,
            BulkConfig = bulkConfig ?? new BulkConfig() { }
        };
        tableInfo.BulkConfig.OperationType = operationType;

        bool isExplicitTransaction = context.Database.GetDbConnection().State == ConnectionState.Open;
        if (tableInfo.BulkConfig.UseTempDB == true && !isExplicitTransaction && (operationType != OperationType.Insert || tableInfo.BulkConfig.SetOutputIdentity))
        {
            throw new InvalidOperationException("When 'UseTempDB' is set then BulkOperation has to be inside Transaction. " +
                                                "Otherwise destination table gets dropped too early because transaction ends before operation is finished."); // throws: 'Cannot access destination table'
        }

        var isDeleteOperation = operationType == OperationType.Delete;
        tableInfo.LoadData(context, type, entities, isDeleteOperation);
        return tableInfo;
    }

    #region Main
    /// <summary>
    /// Configures the table info based on entity data 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="context"></param>
    /// <param name="type"></param>
    /// <param name="entities"></param>
    /// <param name="loadOnlyPKColumn"></param>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="MultiplePropertyListSetException"></exception>
    /// <exception cref="InvalidBulkConfigException"></exception>
    public void LoadData<T>(DbContext context, Type? type, IList<T> entities, bool loadOnlyPKColumn)

    {
        LoadOnlyPKColumn = loadOnlyPKColumn;
        var entityType = type is null ? null : context.Model.FindEntityType(type);
        if (entityType == null)
        {
            type = entities[0]?.GetType() ?? throw new ArgumentNullException(nameof(type));
            entityType = context.Model.FindEntityType(type);
            HasAbstractList = true;
        }
        if (entityType == null)
        {
            throw new InvalidOperationException($"DbContext does not contain EntitySet for Type: { type?.Name }");
        }

        //var relationalData = entityType.Relational(); relationalData.Schema relationalData.TableName // DEPRECATED in Core3.0
        string? providerName = context.Database.ProviderName?.ToLower();
        bool isSqlServer = providerName?.EndsWith(DbServer.SQLServer.ToString().ToLower()) ?? false;
        bool isNpgsql = providerName?.EndsWith(DbServer.PostgreSQL.ToString().ToLower()) ?? false;
        bool isSqlite = providerName?.EndsWith(DbServer.SQLite.ToString().ToLower()) ?? false;

        string? defaultSchema = isSqlServer ? "dbo" : null;

        string? customSchema = null;
        string? customTableName = null;
        if (BulkConfig.CustomDestinationTableName != null)
        {
            customTableName = BulkConfig.CustomDestinationTableName;
            if (customTableName.Contains('.'))
            {
                var tableNameSplitList = customTableName.Split('.');
                customSchema = tableNameSplitList[0];
                customTableName = tableNameSplitList[1];
            }
        }
        Schema = customSchema ?? entityType.GetSchema() ?? defaultSchema;
        var entityTableName = entityType.GetTableName();
        TableName = customTableName ?? entityTableName;

        string? sourceSchema = null;
        string? sourceTableName = null;
        if (BulkConfig.CustomSourceTableName != null)
        {
            sourceTableName = BulkConfig.CustomSourceTableName;
            if (sourceTableName.Contains('.'))
            {
                var tableNameSplitList = sourceTableName.Split('.');
                sourceSchema = tableNameSplitList[0];
                sourceTableName = tableNameSplitList[1];
            }
            BulkConfig.UseTempDB = false;
        }

        TempSchema = sourceSchema ?? Schema;
        TempTableSufix = sourceTableName != null ? "" : "Temp";
        if (BulkConfig.UniqueTableNameTempDb)
        {
            // 8 chars of Guid as tableNameSufix to avoid same name collision with other tables
            TempTableSufix += Guid.NewGuid().ToString()[..8];
            // TODO Consider Hash                                                             
        }
        TempTableName = sourceTableName ?? $"{TableName}{TempTableSufix}";

        if (entityTableName is null)
        {
            throw new ArgumentException("Entity does not contain a table name");
        }

        ObjectIdentifier = StoreObjectIdentifier.Table(entityTableName, entityType.GetSchema());

        var allProperties = new List<IProperty>();
        foreach (var entityProperty in entityType.GetProperties())
        {
            var columnName = entityProperty.GetColumnName(ObjectIdentifier);
            bool isTemporalColumn = columnName is not null
                && entityProperty.IsShadowProperty()
                && entityProperty.ClrType == typeof(DateTime)
                && BulkConfig.TemporalColumns.Contains(columnName);

            HasTemporalColumns = HasTemporalColumns || isTemporalColumn;

            if (columnName == null || isTemporalColumn)
                continue;

            allProperties.Add(entityProperty);
            ColumnNamesTypesDict.Add(columnName, entityProperty.GetColumnType());
            ColumnToPropertyDictionary.Add(columnName, entityProperty);

            if (BulkConfig.DateTime2PrecisionForceRound)
            {
                var columnMappings = entityProperty.GetTableColumnMappings();
                var firstMapping = columnMappings.FirstOrDefault();
                var columnType = firstMapping?.Column.StoreType;
                if ((columnType?.StartsWith("datetime2(") ?? false) && (!columnType?.EndsWith("7)") ?? false))
                {
                    string precisionText = columnType!.Substring(10, 1);
                    int precision = int.Parse(precisionText);
                    DateTime2PropertiesPrecisionLessThen7Dict.Add(firstMapping!.Property.Name, precision); // SqlBulkCopy does Floor instead of Round so Rounding done in memory
                }
            }
        }

        bool areSpecifiedUpdateByProperties = BulkConfig.UpdateByProperties?.Count > 0;
        var primaryKeys = entityType.FindPrimaryKey()?.Properties?.ToDictionary(a => a.Name, b => b.GetColumnName(ObjectIdentifier) ?? string.Empty);
        EntityPKPropertyColumnNameDict = primaryKeys ?? new Dictionary<string, string>();

        HasSinglePrimaryKey = primaryKeys?.Count == 1;
        PrimaryKeysPropertyColumnNameDict = areSpecifiedUpdateByProperties ? BulkConfig.UpdateByProperties?.ToDictionary(a => a, b => allProperties.First(p => p.Name == b).GetColumnName(ObjectIdentifier) ?? string.Empty) ?? new()
                                                                           : (primaryKeys ?? new Dictionary<string, string>());

        // load all derived type properties
        if (entityType.IsAbstract())
        {
            foreach (var derivedType in entityType.GetDirectlyDerivedTypes())
            {
                foreach (var derivedProperty in derivedType.GetProperties())
                {
                    if (!allProperties.Contains(derivedProperty))
                        allProperties.Add(derivedProperty);
                }
            }
        }

        var navigations = entityType.GetNavigations();
        AllNavigationsDictionary = navigations.ToDictionary(nav => nav.Name, nav => nav);

        var ownedTypes = navigations.Where(a => a.TargetEntityType.IsOwned());
        HasOwnedTypes = ownedTypes.Any();
        OwnedTypesDict = ownedTypes.ToDictionary(a => a.Name, a => a);

        if (isSqlServer || isNpgsql)
        {
            string strategyName = (isSqlServer ? nameof(SqlServerValueGenerationStrategy) :
                                                 nameof(Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy))
                                  .Replace("Value", ":Value"); //example 'SqlServer:ValueGenerationStrategy'
            foreach (var property in allProperties)
            {
                var annotation = property.FindAnnotation(strategyName);
                bool hasIdentity = false;
                if (annotation != null)
                    hasIdentity = isSqlServer ? (SqlServerValueGenerationStrategy?)annotation.Value == SqlServerValueGenerationStrategy.IdentityColumn
                                              : (Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy?)annotation.Value == Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn;
                if (hasIdentity == true)
                {
                    IdentityColumnName = property.GetColumnName(ObjectIdentifier);
                    break;
                }
            }
        }
        if (isSqlite) // SQLite no ValueGenerationStrategy
        {
            // for HiLo on SqlServer was returning True when should be False
            IdentityColumnName = allProperties.SingleOrDefault(a => a.IsPrimaryKey() &&
                                                    a.ValueGenerated == ValueGenerated.OnAdd && // ValueGenerated equals OnAdd for nonIdentity column like Guid so take only number types
                                                    (a.ClrType.Name.StartsWith("Byte") ||
                                                     a.ClrType.Name.StartsWith("SByte") ||
                                                     a.ClrType.Name.StartsWith("Int") ||
                                                     a.ClrType.Name.StartsWith("UInt") ||
                                                     (isSqlServer && a.ClrType.Name.StartsWith("Decimal")))
                                              )?.GetColumnName(ObjectIdentifier);
        }

        // timestamp/row version properties are only set by the Db, the property has a [Timestamp] Attribute or is configured in FluentAPI with .IsRowVersion()
        // They can be identified by the columne type "timestamp" or .IsConcurrencyToken in combination with .ValueGenerated == ValueGenerated.OnAddOrUpdate
        string timestampDbTypeName = nameof(TimestampAttribute).Replace("Attribute", "").ToLower(); // = "timestamp";
        IEnumerable<IProperty> timeStampProperties;
        if (BulkConfig.IgnoreRowVersion)
            timeStampProperties = new List<IProperty>();
        else
            timeStampProperties = allProperties.Where(a => a.IsConcurrencyToken && a.ValueGenerated == ValueGenerated.OnAddOrUpdate); // || a.GetColumnType() == timestampDbTypeName // removed as unnecessary and might not be correct

        TimeStampColumnName = timeStampProperties.FirstOrDefault()?.GetColumnName(ObjectIdentifier); // can be only One
        TimeStampPropertyName = timeStampProperties.FirstOrDefault()?.Name; // can be only One
        var allPropertiesExceptTimeStamp = allProperties.Except(timeStampProperties);
        var properties = allPropertiesExceptTimeStamp.Where(a => a.GetComputedColumnSql() == null);

        var propertiesWithDefaultValues = allPropertiesExceptTimeStamp.Where(a =>
            !a.IsShadowProperty() &&
            (a.GetDefaultValueSql() != null ||
             (a.GetDefaultValue() != null &&
              a.ValueGenerated != ValueGenerated.Never &&
              a.ClrType != typeof(Guid)) // Since .Net_6.0 in EF 'Guid' type has DefaultValue even when not explicitly defined with Annotation or FluentApi
            ));
        foreach (var propertyWithDefaultValue in propertiesWithDefaultValues)
        {
            var propertyType = propertyWithDefaultValue.ClrType;
            var instance = propertyType.IsValueType || propertyType.GetConstructor(Type.EmptyTypes) != null
                              ? Activator.CreateInstance(propertyType)
                              : null; // when type does not have parameterless constructor, like String for example, then default value is 'null'

            bool listHasAllDefaultValues = !entities.Any(a => a?.GetType().GetProperty(propertyWithDefaultValue.Name)?.GetValue(a, null)?.ToString() != instance?.ToString());
            // it is not feasible to have in same list simultaneously both entities groups With and Without default values, they are omitted OnInsert only if all have default values or if it is PK (like Guid DbGenerated)
            if (listHasAllDefaultValues || (PrimaryKeysPropertyColumnNameDict.ContainsKey(propertyWithDefaultValue.Name) && propertyType == typeof(Guid)))
            {
                DefaultValueProperties.Add(propertyWithDefaultValue.Name);
            }
        }

        var propertiesOnCompare = allPropertiesExceptTimeStamp.Where(a => a.GetComputedColumnSql() == null);
        var propertiesOnUpdate = allPropertiesExceptTimeStamp.Where(a => a.GetComputedColumnSql() == null);

        // TimeStamp prop. is last column in OutputTable since it is added later with varbinary(8) type in which Output can be inserted
        var outputProperties = allPropertiesExceptTimeStamp.Where(a => a.GetColumnName(ObjectIdentifier) != null).Concat(timeStampProperties);
        OutputPropertyColumnNamesDict = outputProperties.ToDictionary(a => a.Name, b => b.GetColumnName(ObjectIdentifier)?.Replace("]", "]]") ?? string.Empty); // square brackets have to be escaped

        bool AreSpecifiedPropertiesToInclude = BulkConfig.PropertiesToInclude?.Count > 0;
        bool AreSpecifiedPropertiesToExclude = BulkConfig.PropertiesToExclude?.Count > 0;

        bool AreSpecifiedPropertiesToIncludeOnCompare = BulkConfig.PropertiesToIncludeOnCompare?.Count > 0;
        bool AreSpecifiedPropertiesToExcludeOnCompare = BulkConfig.PropertiesToExcludeOnCompare?.Count > 0;

        bool AreSpecifiedPropertiesToIncludeOnUpdate = BulkConfig.PropertiesToIncludeOnUpdate?.Count > 0;
        bool AreSpecifiedPropertiesToExcludeOnUpdate = BulkConfig.PropertiesToExcludeOnUpdate?.Count > 0;

        if (AreSpecifiedPropertiesToInclude)
        {
            if (areSpecifiedUpdateByProperties) // Adds UpdateByProperties to PropertyToInclude if they are not already explicitly listed
            {
                if (BulkConfig.UpdateByProperties is not null)
                {
                    foreach (var updateByProperty in BulkConfig.UpdateByProperties)
                    {
                        if (!BulkConfig.PropertiesToInclude?.Contains(updateByProperty) ?? false)
                        {
                            BulkConfig.PropertiesToInclude?.Add(updateByProperty);
                        }
                    }
                }
            }
            else // Adds PrimaryKeys to PropertyToInclude if they are not already explicitly listed
            {
                foreach (var primaryKey in PrimaryKeysPropertyColumnNameDict)
                {
                    if (!BulkConfig.PropertiesToInclude?.Contains(primaryKey.Key) ?? false)
                    {
                        BulkConfig.PropertiesToInclude?.Add(primaryKey.Key);
                    }
                }
            }
        }

        foreach (var property in allProperties)
        {
            if (property.PropertyInfo != null) // skip Shadow Property
            {
                FastPropertyDict.Add(property.Name, new FastProperty(property.PropertyInfo));
            }

            var converter = property.GetTypeMapping().Converter;
            if (converter is not null)
            {
                var columnName = property.GetColumnName(ObjectIdentifier) ?? string.Empty;
                ConvertiblePropertyColumnDict.Add(property.Name, columnName);
                ConvertibleColumnConverterDict.Add(columnName, converter);
            }
        }

        UpdateByPropertiesAreNullable = properties.Any(a => PrimaryKeysPropertyColumnNameDict.ContainsKey(a.Name) && a.IsNullable);

        if (AreSpecifiedPropertiesToInclude || AreSpecifiedPropertiesToExclude)
        {
            if (AreSpecifiedPropertiesToInclude && AreSpecifiedPropertiesToExclude)
            {
                throw new MultiplePropertyListSetException(nameof(BulkConfig.PropertiesToInclude), nameof(BulkConfig.PropertiesToExclude));
            }
            if (AreSpecifiedPropertiesToInclude)
            {
                properties = properties.Where(a => BulkConfig.PropertiesToInclude?.Contains(a.Name) ?? false);
                ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToInclude, nameof(BulkConfig.PropertiesToInclude));
            }
            if (AreSpecifiedPropertiesToExclude)
            {
                properties = properties.Where(a => !BulkConfig.PropertiesToExclude?.Contains(a.Name) ?? false);
                ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToExclude, nameof(BulkConfig.PropertiesToExclude));
            }
        }

        if (AreSpecifiedPropertiesToIncludeOnCompare || AreSpecifiedPropertiesToExcludeOnCompare)
        {
            if (AreSpecifiedPropertiesToIncludeOnCompare && AreSpecifiedPropertiesToExcludeOnCompare)
            {
                throw new MultiplePropertyListSetException(nameof(BulkConfig.PropertiesToIncludeOnCompare), nameof(BulkConfig.PropertiesToExcludeOnCompare));
            }
            if (AreSpecifiedPropertiesToIncludeOnCompare)
            {
                propertiesOnCompare = propertiesOnCompare.Where(a => BulkConfig.PropertiesToIncludeOnCompare?.Contains(a.Name) ?? false);
                ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToIncludeOnCompare, nameof(BulkConfig.PropertiesToIncludeOnCompare));
            }
            if (AreSpecifiedPropertiesToExcludeOnCompare)
            {
                propertiesOnCompare = propertiesOnCompare.Where(a => !BulkConfig.PropertiesToExcludeOnCompare?.Contains(a.Name) ?? false);
                ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToExcludeOnCompare, nameof(BulkConfig.PropertiesToExcludeOnCompare));
            }
        }
        else
        {
            propertiesOnCompare = properties;
        }
        if (AreSpecifiedPropertiesToIncludeOnUpdate || AreSpecifiedPropertiesToExcludeOnUpdate)
        {
            if (AreSpecifiedPropertiesToIncludeOnUpdate && AreSpecifiedPropertiesToExcludeOnUpdate)
            {
                throw new MultiplePropertyListSetException(nameof(BulkConfig.PropertiesToIncludeOnUpdate), nameof(BulkConfig.PropertiesToExcludeOnUpdate));
            }
            if (AreSpecifiedPropertiesToIncludeOnUpdate)
            {
                propertiesOnUpdate = propertiesOnUpdate.Where(a => BulkConfig.PropertiesToIncludeOnUpdate?.Contains(a.Name) ?? false);
                ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToIncludeOnUpdate, nameof(BulkConfig.PropertiesToIncludeOnUpdate));
            }
            if (AreSpecifiedPropertiesToExcludeOnUpdate)
            {
                propertiesOnUpdate = propertiesOnUpdate.Where(a => !BulkConfig.PropertiesToExcludeOnUpdate?.Contains(a.Name) ?? false);
                ValidateSpecifiedPropertiesList(BulkConfig.PropertiesToExcludeOnUpdate, nameof(BulkConfig.PropertiesToExcludeOnUpdate));
            }
        }
        else
        {
            propertiesOnUpdate = properties;

            if (BulkConfig.UpdateByProperties != null) // to remove NonIdentity PK like Guid from SET ID = ID, ...
            {
                propertiesOnUpdate = propertiesOnUpdate.Where(a => !BulkConfig.UpdateByProperties.Contains(a.Name));
            }
            else if (primaryKeys != null)
            {
                propertiesOnUpdate = propertiesOnUpdate.Where(a => !primaryKeys.ContainsKey(a.Name));
            }
        }

        PropertyColumnNamesCompareDict = propertiesOnCompare.ToDictionary(a => a.Name, b => b.GetColumnName(ObjectIdentifier)?.Replace("]", "]]") ?? string.Empty);
        PropertyColumnNamesUpdateDict = propertiesOnUpdate.ToDictionary(a => a.Name, b => b.GetColumnName(ObjectIdentifier)?.Replace("]", "]]") ?? string.Empty);

        if (loadOnlyPKColumn)
        {
            if (PrimaryKeysPropertyColumnNameDict.Count == 0)
                throw new InvalidBulkConfigException("If no PrimaryKey is defined operation requres bulkConfig set with 'UpdatedByProperties'.");
            PropertyColumnNamesDict = properties.Where(a => PrimaryKeysPropertyColumnNameDict.ContainsKey(a.Name)).ToDictionary(a => a.Name, b => b.GetColumnName(ObjectIdentifier)?.Replace("]", "]]") ?? string.Empty);
        }
        else
        {
            PropertyColumnNamesDict = properties.ToDictionary(a => a.Name, b => b.GetColumnName(ObjectIdentifier)?.Replace("]", "]]") ?? string.Empty);
            ShadowProperties = new HashSet<string>(properties.Where(p => p.IsShadowProperty() && !p.IsForeignKey()).Select(p => p.GetColumnName(ObjectIdentifier) ?? string.Empty));

            foreach (var navigation in entityType.GetNavigations().Where(a => !a.IsCollection && !a.TargetEntityType.IsOwned()))
            {
                if (navigation.PropertyInfo is not null)
                {
                    FastPropertyDict.Add(navigation.Name, new FastProperty(navigation.PropertyInfo));
                }
            }

            if (HasOwnedTypes)  // Support owned entity property update. TODO: Optimize
            {
                foreach (var navigationProperty in ownedTypes)
                {
                    var property = navigationProperty.PropertyInfo;
                    FastPropertyDict.Add(property!.Name, new FastProperty(property));

                    // If the OwnedType is mapped to the separate table, don't try merge it into its owner
                    if (OwnedTypeUtil.IsOwnedInSameTableAsOwner(navigationProperty) == false)
                        continue;

                    //Type navOwnedType = type?.Assembly.GetType(property.PropertyType.FullName!) ?? throw new ArgumentException("Unable to determine Type"); // was not used
                    var ownedEntityType = context.Model.FindEntityType(property.PropertyType);
                    if (ownedEntityType == null) // when entity has more then one ownedType (e.g. Address HomeAddress, Address WorkAddress) or one ownedType is in multiple Entities like Audit is usually.
                    {
                        ownedEntityType = context.Model.GetEntityTypes().SingleOrDefault(x => x.ClrType == property.PropertyType && x.Name.StartsWith(entityType.Name + "." + property.Name + "#"));
                    }
                    var ownedEntityProperties = ownedEntityType?.GetProperties().ToList() ?? new();
                    var ownedEntityPropertyNameColumnNameDict = new Dictionary<string, string>();

                    foreach (var ownedEntityProperty in ownedEntityProperties)
                    {
                        if (!ownedEntityProperty.IsPrimaryKey())
                        {
                            string columnName = ownedEntityProperty.GetColumnName(ObjectIdentifier) ?? string.Empty;
                            ownedEntityPropertyNameColumnNameDict.Add(ownedEntityProperty.Name, columnName);
                            var ownedEntityPropertyFullName = property.Name + "_" + ownedEntityProperty.Name;
                            if (!FastPropertyDict.ContainsKey(ownedEntityPropertyFullName) && ownedEntityProperty.PropertyInfo is not null)
                            {
                                FastPropertyDict.Add(ownedEntityPropertyFullName, new FastProperty(ownedEntityProperty.PropertyInfo));
                            }
                        }

                        var converter = ownedEntityProperty.GetValueConverter();
                        if (converter != null)
                        {
                            ConvertibleColumnConverterDict.Add($"{navigationProperty.Name}_{ownedEntityProperty.Name}", converter);
                        }
                    }
                    var ownedProperties = property.PropertyType.GetProperties();
                    foreach (var ownedProperty in ownedProperties)
                    {
                        if (ownedEntityPropertyNameColumnNameDict.ContainsKey(ownedProperty.Name))
                        {
                            string ownedPropertyFullName = property.Name + "." + ownedProperty.Name;
                            var ownedPropertyType = Nullable.GetUnderlyingType(ownedProperty.PropertyType) ?? ownedProperty.PropertyType;

                            bool doAddProperty = true;
                            if (AreSpecifiedPropertiesToInclude && !(BulkConfig.PropertiesToInclude?.Contains(ownedPropertyFullName) ?? false))
                            {
                                doAddProperty = false;
                            }
                            if (AreSpecifiedPropertiesToExclude && (BulkConfig.PropertiesToExclude?.Contains(ownedPropertyFullName) ?? false))
                            {
                                doAddProperty = false;
                            }

                            if (doAddProperty)
                            {
                                string columnName = ownedEntityPropertyNameColumnNameDict[ownedProperty.Name];
                                PropertyColumnNamesDict.Add(ownedPropertyFullName, columnName);
                                PropertyColumnNamesCompareDict.Add(ownedPropertyFullName, columnName);
                                PropertyColumnNamesUpdateDict.Add(ownedPropertyFullName, columnName);
                                OutputPropertyColumnNamesDict.Add(ownedPropertyFullName, columnName);
                            }
                        }
                    }
                }
            }
        }
    }


    /// <summary>
    /// Validates the specified property list
    /// </summary>
    /// <param name="specifiedPropertiesList"></param>
    /// <param name="specifiedPropertiesListName"></param>
    /// <exception cref="InvalidOperationException"></exception>
    protected void ValidateSpecifiedPropertiesList(List<string>? specifiedPropertiesList, string specifiedPropertiesListName)

    {
        if (specifiedPropertiesList is not null)
        {
            foreach (var configSpecifiedPropertyName in specifiedPropertiesList)
            {

                if (!FastPropertyDict.Any(a => a.Key == configSpecifiedPropertyName) &&
                    !configSpecifiedPropertyName.Contains('.') && // Those with dot "." skiped from validating for now since FastPropertyDict here does not contain them
                    !(specifiedPropertiesListName == nameof(BulkConfig.PropertiesToIncludeOnUpdate) && configSpecifiedPropertyName == "") && // In PropsToIncludeOnUpdate empty is allowed as config for skipping Update
                    !BulkConfig.TemporalColumns.Contains(configSpecifiedPropertyName)
                    )
                {
                    throw new InvalidOperationException($"PropertyName '{configSpecifiedPropertyName}' specified in '{specifiedPropertiesListName}' not found in Properties.");
                }
            }
        }
    }

    /// <summary>
    /// Supports <see cref="Microsoft.Data.SqlClient.SqlBulkCopy"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="sqlBulkCopy"></param>
    /// <param name="entities"></param>
    /// <param name="setColumnMapping"></param>
    /// <param name="progress"></param>
    public void SetSqlBulkCopyConfig<T>(Microsoft.Data.SqlClient.SqlBulkCopy sqlBulkCopy, IList<T> entities, bool setColumnMapping, Action<decimal>? progress)
    {
        sqlBulkCopy.DestinationTableName = InsertToTempTable ? FullTempTableName : FullTableName;
        sqlBulkCopy.BatchSize = BulkConfig.BatchSize;
        sqlBulkCopy.NotifyAfter = BulkConfig.NotifyAfter ?? BulkConfig.BatchSize;
        sqlBulkCopy.SqlRowsCopied += (sender, e) =>
        {
            progress?.Invoke(ProgressHelper.GetProgress(entities.Count, e.RowsCopied)); // round to 4 decimal places
        };
        sqlBulkCopy.BulkCopyTimeout = BulkConfig.BulkCopyTimeout ?? sqlBulkCopy.BulkCopyTimeout;
        sqlBulkCopy.EnableStreaming = BulkConfig.EnableStreaming;

        if (setColumnMapping)
        {
            foreach (var element in PropertyColumnNamesDict)
            {
                sqlBulkCopy.ColumnMappings.Add(element.Key, element.Value);
            }
        }
    }
    #endregion

    #region SqlCommands
      
    /// <summary>
    /// Checks if the table exists
    /// </summary>
    /// <param name="context"></param>
    /// <param name="tableInfo"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="isAsync"></param>
    /// <returns></returns>
    public static async Task<bool> CheckTableExistAsync(DbContext context, TableInfo tableInfo, bool isAsync, CancellationToken cancellationToken)
    {
        if (isAsync)
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.Database.OpenConnection();
        }

        bool tableExist = false;
        try
        {
            var sqlConnection = context.Database.GetDbConnection();
            var currentTransaction = context.Database.CurrentTransaction;

            using var command = sqlConnection.CreateCommand();
            if (currentTransaction != null)
                command.Transaction = currentTransaction.GetDbTransaction();
            command.CommandText = SqlQueryBuilder.CheckTableExist(tableInfo.FullTempTableName, tableInfo.BulkConfig.UseTempDB);

            if (isAsync)
            {
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (reader.HasRows)
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        tableExist = (int)reader[0] == 1;
                    }
                }
            }
            else
            {
                using var reader = command.ExecuteReader();
                if (reader.HasRows)
                {
                    while (reader.Read())
                    {
                        tableExist = (int)reader[0] == 1;
                    }
                }
            }
        }
        finally
        {
            if (isAsync)
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            else
            {
                context.Database.CloseConnection();
            }
        }
        return tableExist;
    }
  
    /// <summary>
    /// Checks the number of updated entities
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="isAsync"></param>
    /// <returns></returns>
    protected async Task<int> GetNumberUpdatedAsync(DbContext context, bool isAsync, CancellationToken cancellationToken)
    {
        var resultParameter = (IDbDataParameter?)Activator.CreateInstance(typeof(Microsoft.Data.SqlClient.SqlParameter));
        if (resultParameter is null)
        {
            throw new ArgumentException("Unable to create an instance of IDbDataParameter");
        }
        resultParameter.ParameterName = "@result";
        resultParameter.DbType = DbType.Int32;
        resultParameter.Direction = ParameterDirection.Output;
        string sqlQueryCount = SqlQueryBuilder.SelectCountIsUpdateFromOutputTable(this);

        var sqlSetResult = $"SET @result = ({sqlQueryCount});";
        if (isAsync)
        {
            await context.Database.ExecuteSqlRawAsync(sqlSetResult, new object[] { resultParameter }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.Database.ExecuteSqlRaw(sqlSetResult, resultParameter);
        }
        return (int)resultParameter.Value!;
    }

    /// <summary>
    /// Checks the number of deleted entities
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="isAsync"></param>
    /// <returns></returns>
    protected async Task<int> GetNumberDeletedAsync(DbContext context, bool isAsync, CancellationToken cancellationToken)
    {
        var resultParameter = (IDbDataParameter?)Activator.CreateInstance(typeof(Microsoft.Data.SqlClient.SqlParameter));
        if (resultParameter is null)
        {
            throw new ArgumentException("Unable to create an instance of IDbDataParameter");
        }
        resultParameter.ParameterName = "@result";
        resultParameter.DbType = DbType.Int32;
        resultParameter.Direction = ParameterDirection.Output;
        string sqlQueryCount = SqlQueryBuilder.SelectCountIsDeleteFromOutputTable(this);

        var sqlSetResult = $"SET @result = ({sqlQueryCount});";
        if (isAsync)
        {
            await context.Database.ExecuteSqlRawAsync(sqlSetResult, new object[] { resultParameter }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.Database.ExecuteSqlRaw(sqlSetResult, resultParameter);
        }
        return (int)resultParameter.Value!;
    }

    #endregion

    /// <summary>
    /// Returns the unique property values
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="propertiesNames"></param>
    /// <param name="fastPropertyDict"></param>
    /// <returns></returns>
    public static string GetUniquePropertyValues(object entity, List<string> propertiesNames, Dictionary<string, FastProperty> fastPropertyDict)
    {
        StringBuilder uniqueBuilder = new(1024);
        string delimiter = "_"; // TODO: Consider making it Config-urable
        foreach (var propertyName in propertiesNames)
        {
            var property = fastPropertyDict[propertyName].Get(entity);
            if (property is Array propertyArray)
            {
                foreach (var element in propertyArray)
                {
                    uniqueBuilder.Append(element?.ToString() ?? "null");
                }
            }
            else
            {
                uniqueBuilder.Append(property?.ToString() ?? "null");
            }

            uniqueBuilder.Append(delimiter);
        }
        string result = uniqueBuilder.ToString();
        result = result[0..^1]; // removes last delimiter
        return result;
    }

    #region ReadProcedures
    /// <summary>
    /// Configures the bulk read column names for the table info
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, string> ConfigureBulkReadTableInfo()
    {
        InsertToTempTable = true;

        var previousPropertyColumnNamesDict = PropertyColumnNamesDict;
        BulkConfig.PropertiesToInclude = PrimaryKeysPropertyColumnNameDict.Select(a => a.Key).ToList();
        PropertyColumnNamesDict = PropertyColumnNamesDict.Where(a => PrimaryKeysPropertyColumnNameDict.ContainsKey(a.Key)).ToDictionary(a => a.Key, a => a.Value);
        return previousPropertyColumnNamesDict;
    }

    internal void UpdateReadEntities<T>(IList<T> entities, IList<T> existingEntities, DbContext context)
    {
        List<string> propertyNames = PropertyColumnNamesDict.Keys.ToList();
        if (HasOwnedTypes)
        {
            foreach (string ownedTypeName in OwnedTypesDict.Keys)
            {
                var ownedTypeProperties = OwnedTypesDict[ownedTypeName].ClrType.GetProperties();
                foreach (var ownedTypeProperty in ownedTypeProperties)
                {
                    propertyNames.Remove(ownedTypeName + "." + ownedTypeProperty.Name);
                }
                propertyNames.Add(ownedTypeName);
            }
        }

        List<string> selectByPropertyNames = PropertyColumnNamesDict.Keys
            .Where(a => PrimaryKeysPropertyColumnNameDict.ContainsKey(a)).ToList();

        Dictionary<string, T> existingEntitiesDict = new();
        foreach (var existingEntity in existingEntities)
        {
            string uniqueProperyValues = GetUniquePropertyValues(existingEntity!, selectByPropertyNames, FastPropertyDict);
            existingEntitiesDict.TryAdd(uniqueProperyValues, existingEntity);
        }

        for (int i = 0; i < NumberOfEntities; i++)
        {
            T entity = entities[i];
            string uniqueProperyValues = GetUniquePropertyValues(entity!, selectByPropertyNames, FastPropertyDict);

            existingEntitiesDict.TryGetValue(uniqueProperyValues, out T? existingEntity);
            bool isPostgreSQL = context.Database.ProviderName?.EndsWith(DbServer.PostgreSQL.ToString()) ?? false;
            if (existingEntity == null && isPostgreSQL)
            {
                existingEntity = existingEntities[i]; // TODO check if BinaryImport with COPY on Postgres preserves order
            }
            if (existingEntity != null)
            {
                foreach (var propertyName in propertyNames)
                {
                    var propertyValue = FastPropertyDict[propertyName].Get(existingEntity);
                    FastPropertyDict[propertyName].Set(entity!, propertyValue);
                }
            }
        }
    }
    #endregion
    /// <summary>
    /// Sets the identity preserve order
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="tableInfo"></param>
    /// <param name="entities"></param>
    /// <param name="reset"></param>
    public void CheckToSetIdentityForPreserveOrder<T>(TableInfo tableInfo, IList<T> entities, bool reset = false)
    {
        string identityPropertyName = PropertyColumnNamesDict.SingleOrDefault(a => a.Value == IdentityColumnName).Key;

        bool doSetIdentityColumnsForInsertOrder = BulkConfig.PreserveInsertOrder &&
                                                  entities.Count > 1 &&
                                                  PrimaryKeysPropertyColumnNameDict?.Count == 1 &&
                                                  PrimaryKeysPropertyColumnNameDict?.Select(a => a.Value).First() == IdentityColumnName;

        var operationType = tableInfo.BulkConfig.OperationType;
        if (doSetIdentityColumnsForInsertOrder == true)
        {
            if (operationType == OperationType.Insert && // Insert should either have all zeros for automatic order, or they can be manually set
                Convert.ToInt64(FastPropertyDict[identityPropertyName].Get(entities[0]!)) != 0) // (to check it fast, condition for all 0s is only done on first one)
            {
                doSetIdentityColumnsForInsertOrder = false;
            }
        }

        if (doSetIdentityColumnsForInsertOrder)
        {
            bool sortEntities = !reset && BulkConfig.SetOutputIdentity &&
                                (operationType == OperationType.Update || operationType == OperationType.InsertOrUpdate || operationType == OperationType.InsertOrUpdateOrDelete);
            var entitiesExistingDict = new Dictionary<long, T>();
            var entitiesNew = new List<T>();
            var entitiesSorted = new List<T>();

            long i = -entities.Count;
            foreach (var entity in entities)
            {
                var identityFastProperty = FastPropertyDict[identityPropertyName];
                long identityValue = Convert.ToInt64(identityFastProperty.Get(entity!));

                if (identityValue == 0 ||         // set only zero(0) values
                    (identityValue < 0 && reset)) // set only negative(-N) values if reset
                {
                    long value = reset ? 0 : i;
                    object idValue;
                    var idType = identityFastProperty.Property.PropertyType;
                    if (idType == typeof(ushort))
                        idValue = (ushort)value;
                    if (idType == typeof(short))
                        idValue = (short)value;
                    else if (idType == typeof(uint))
                        idValue = (uint)value;
                    else if (idType == typeof(int))
                        idValue = (int)value;
                    else if (idType == typeof(ulong))
                        idValue = (ulong)value;
                    else if (idType == typeof(decimal))
                        idValue = (decimal)value;
                    else
                        idValue = value; // type 'long' left as default

                    identityFastProperty.Set(entity!, idValue);
                    i++;
                }
                if (sortEntities)
                {
                    if (identityValue != 0)
                        entitiesExistingDict.Add(identityValue, entity); // first load existing ones
                    else
                        entitiesNew.Add(entity);
                }
            }
            if (sortEntities)
            {
                entitiesSorted = entitiesExistingDict.OrderBy(a => a.Key).Select(a => a.Value).ToList();
                entitiesSorted.AddRange(entitiesNew); // then append new ones
                tableInfo.EntitiesSortedReference = entitiesSorted.Cast<object>().ToList();
            }
        }
    }

    /// <summary>
    /// Loads the ouput entities
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="context"></param>
    /// <param name="type"></param>
    /// <param name="sqlSelect"></param>
    /// <returns></returns>
    public List<T> LoadOutputEntities<T>(DbContext context, Type type, string sqlSelect) where T : class
    {
        List<T> existingEntities;
        if (typeof(T) == type)
        {
            Expression<Func<DbContext, IQueryable<T>>> expression = GetQueryExpression<T>(sqlSelect, false);
            var compiled = EF.CompileQuery(expression); // instead using Compiled queries
            existingEntities = compiled(context).ToList();
        }
        else // TODO: Consider removing
        {
            Expression<Func<DbContext, IEnumerable>> expression = GetQueryExpression(type, sqlSelect, false);
            var compiled = EF.CompileQuery(expression); // instead using Compiled queries
            existingEntities = compiled(context).Cast<T>().ToList();
        }
        return existingEntities;
    }
  
    /// <summary>
    /// Updates the entities' identity field
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="tableInfo"></param>
    /// <param name="entities"></param>
    /// <param name="entitiesWithOutputIdentity"></param>
    public void UpdateEntitiesIdentity<T>(TableInfo tableInfo, IList<T> entities, IList<object> entitiesWithOutputIdentity)
    {
        var identifierPropertyName = IdentityColumnName != null ? OutputPropertyColumnNamesDict.SingleOrDefault(a => a.Value == IdentityColumnName).Key // it Identity autoincrement 
                                                                : PrimaryKeysPropertyColumnNameDict.FirstOrDefault().Key;                               // or PK with default sql value

        if (BulkConfig.PreserveInsertOrder) // Updates Db changed Columns in entityList
        {
            int countDiff = entities.Count - entitiesWithOutputIdentity.Count;
            if (countDiff > 0) // When some ommited from Merge because of TimeStamp conflict then changes are not loaded but output is set in TimeStampInfo
            {
                tableInfo.BulkConfig.TimeStampInfo = new TimeStampInfo
                {
                    NumberOfSkippedForUpdate = countDiff,
                    EntitiesOutput = entitiesWithOutputIdentity.Cast<object>().ToList()
                };
                return;
            }

            if (tableInfo.EntitiesSortedReference != null)
            {
                entities = tableInfo.EntitiesSortedReference.Cast<T>().ToList();
            }

            var entitiesDict = new Dictionary<object, T>();
            var numberOfOutputEntities = Math.Min(NumberOfEntities, entitiesWithOutputIdentity.Count);
            for (int i = 0; i < numberOfOutputEntities; i++)
            {
                if (identifierPropertyName != null)
                {
                    var customPK = tableInfo.PrimaryKeysPropertyColumnNameDict.FirstOrDefault().Value;
                    if (identifierPropertyName != customPK &&
                        (tableInfo.BulkConfig.OperationType == OperationType.Update ||
                         tableInfo.BulkConfig.OperationType == OperationType.InsertOrUpdate ||
                         tableInfo.BulkConfig.OperationType == OperationType.InsertOrUpdateOrDelete)
                       ) // (UpsertOrderTest) fix for BulkInsertOrUpdate assigns wrong output IDs when PreserveInsertOrder = true and SetOutputIdentity = true
                    {
                        if (entitiesDict.Count == 0)
                        {
                            foreach (var entity in entities)
                            {
                                var customPKValue = FastPropertyDict[customPK].Get(entity!);
                                entitiesDict.Add(customPKValue!, entity);
                            }
                        }
                        var identityPropertyValue = FastPropertyDict[identifierPropertyName].Get(entitiesWithOutputIdentity[i]);
                        var customPKOutputValue = FastPropertyDict[customPK].Get(entitiesWithOutputIdentity[i]);
                        if (customPKOutputValue is not null)
                        {
                            FastPropertyDict[identifierPropertyName].Set(entitiesDict[customPKOutputValue]!, identityPropertyValue);
                        }

                    }
                    else
                    {
                        var identityPropertyValue = FastPropertyDict[identifierPropertyName].Get(entitiesWithOutputIdentity[i]);
                        FastPropertyDict[identifierPropertyName].Set(entities[i]!, identityPropertyValue);
                    }
                }

                if (TimeStampColumnName != null) // timestamp/rowversion is also generated by the SqlServer so if exist should be updated as well
                {
                    string timeStampPropertyName = OutputPropertyColumnNamesDict.SingleOrDefault(a => a.Value == TimeStampColumnName).Key;
                    var timeStampPropertyValue = FastPropertyDict[timeStampPropertyName].Get(entitiesWithOutputIdentity[i]);
                    FastPropertyDict[timeStampPropertyName].Set(entities[i]!, timeStampPropertyValue);
                }

                var propertiesToLoad = tableInfo.OutputPropertyColumnNamesDict.Keys.Where(a => a != identifierPropertyName && a != TimeStampColumnName && // already loaded in segmet above
                                                                                               (tableInfo.DefaultValueProperties.Contains(a) ||           // add Computed and DefaultValues
                                                                                                !tableInfo.PropertyColumnNamesDict.ContainsKey(a)));      // remove others since already have same have (could be omited)
                foreach (var outputPropertyName in propertiesToLoad)
                {
                    var propertyValue = FastPropertyDict[outputPropertyName].Get(entitiesWithOutputIdentity[i]);
                    FastPropertyDict[outputPropertyName].Set(entities[i]!, propertyValue);
                }
            }
        }
        else // Clears entityList and then refills it with loaded entites from Db
        {
            entities.Clear();
            if (typeof(T) == entitiesWithOutputIdentity.FirstOrDefault()?.GetType())
            {
                ((List<T>)entities).AddRange(entitiesWithOutputIdentity.Cast<T>().ToList());
            }
            else
            {
                var entitiesObjects = entities.Cast<object>().ToList();
                entitiesObjects.AddRange(entitiesWithOutputIdentity);
            }
        }
    }

    // Compiled queries created manually to avoid EF Memory leak bug when using EF with dynamic SQL:
    // https://github.com/borisdj/EFCore.BulkExtensions/issues/73
    // Once the following Issue gets fixed(expected in EF 3.0) this can be replaced with code segment: DirectQuery
    // https://github.com/aspnet/EntityFrameworkCore/issues/12905
    #region CompiledQuery
    /// <summary>
    /// Loads the output data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="context"></param>
    /// <param name="type"></param>
    /// <param name="entities"></param>
    /// <param name="tableInfo"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="isAsync"></param>
    /// <returns></returns>
    public async Task LoadOutputDataAsync<T>(DbContext context, Type type, IList<T> entities, TableInfo tableInfo, bool isAsync, CancellationToken cancellationToken) where T : class
    {
        bool hasIdentity = OutputPropertyColumnNamesDict.Any(a => a.Value == IdentityColumnName) ||
                           (tableInfo.HasSinglePrimaryKey && tableInfo.DefaultValueProperties.Contains(tableInfo.PrimaryKeysPropertyColumnNameDict.FirstOrDefault().Key));
        int totalNumber = entities.Count;
        if (BulkConfig.SetOutputIdentity && hasIdentity)
        {
            string sqlQuery = SqlQueryBuilder.SelectFromOutputTable(this);
            //var entitiesWithOutputIdentity = await QueryOutputTableAsync<T>(context, sqlQuery).ToListAsync(cancellationToken).ConfigureAwait(false); // TempFIX
            var entitiesWithOutputIdentity = QueryOutputTable(context, type, sqlQuery).Cast<object>().ToList();
            //var entitiesWithOutputIdentity = (typeof(T) == type) ? QueryOutputTable<object>(context, sqlQuery).ToList() : QueryOutputTable(context, type, sqlQuery).Cast<object>().ToList();

            //var entitiesObjects = entities.Cast<object>().ToList();
            UpdateEntitiesIdentity(tableInfo, entities, entitiesWithOutputIdentity);
            totalNumber = entitiesWithOutputIdentity.Count;
        }
        if (BulkConfig.CalculateStats)
        {
            int numberUpdated;
            int numberDeleted;
            if (isAsync)
            {
                numberUpdated = await GetNumberUpdatedAsync(context, isAsync: true, cancellationToken).ConfigureAwait(false);
                numberDeleted = await GetNumberDeletedAsync(context, isAsync: true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                numberUpdated = GetNumberUpdatedAsync(context, isAsync: false, cancellationToken).GetAwaiter().GetResult();
                numberDeleted = GetNumberDeletedAsync(context, isAsync: false, cancellationToken).GetAwaiter().GetResult();
            }
            BulkConfig.StatsInfo = new StatsInfo
            {
                StatsNumberUpdated = numberUpdated,
                StatsNumberDeleted = numberDeleted,
                StatsNumberInserted = totalNumber - numberUpdated - numberDeleted
            };
        }
    }

    /// <summary>
    /// Queries the output table data
    /// </summary>
    /// <param name="context"></param>
    /// <param name="type"></param>
    /// <param name="sqlQuery"></param>
    /// <returns></returns>
    protected IEnumerable QueryOutputTable(DbContext context, Type type, string sqlQuery)
    {
        var compiled = EF.CompileQuery(GetQueryExpression(type, sqlQuery));
        var result = compiled(context);
        return result;
    }

    /*protected IEnumerable<T> QueryOutputTable<T>(DbContext context, string sqlQuery) where T : class
    {
        var compiled = EF.CompileQuery(GetQueryExpression<T>(sqlQuery));
        var result = compiled(context);
        return result;
    }*/

    /*protected IAsyncEnumerable<T> QueryOutputTableAsync<T>(DbContext context, string sqlQuery) where T : class
    {
        var compiled = EF.CompileAsyncQuery(GetQueryExpression<T>(sqlQuery));
        var result = compiled(context);
        return result;
    }*/

    /// <summary>
    /// Returns an expression for the SQL query
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="sqlQuery"></param>
    /// <param name="ordered"></param>
    /// <returns></returns>
    public Expression<Func<DbContext, IQueryable<T>>> GetQueryExpression<T>(string sqlQuery, bool ordered = true) where T : class
    {
        Expression<Func<DbContext, IQueryable<T>>>? expression = null;
        if (BulkConfig.TrackingEntities) // If Else can not be replaced with Ternary operator for Expression
        {
            expression = BulkConfig.IgnoreGlobalQueryFilters ?
                (ctx) => ctx.Set<T>().FromSqlRaw(sqlQuery).IgnoreQueryFilters() :
                (ctx) => ctx.Set<T>().FromSqlRaw(sqlQuery);
        }
        else
        {
            expression = BulkConfig.IgnoreGlobalQueryFilters ?
                (ctx) => ctx.Set<T>().FromSqlRaw(sqlQuery).AsNoTracking().IgnoreQueryFilters() :
                (ctx) => ctx.Set<T>().FromSqlRaw(sqlQuery).AsNoTracking();
        }
        return ordered ?
            Expression.Lambda<Func<DbContext, IQueryable<T>>>(OrderBy(typeof(T), expression.Body, PrimaryKeysPropertyColumnNameDict.Select(a => a.Key).ToList()), expression.Parameters) :
            expression;

        // ALTERNATIVELY OrderBy with DynamicLinq ('using System.Linq.Dynamic.Core;' NuGet required) that eliminates need for custom OrderBy<T> method with Expression.
        //var queryOrdered = query.OrderBy(PrimaryKeys[0]);
    }

    /// <summary>
    /// Returns an expression for the SQL query
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="sqlQuery"></param>
    /// <param name="ordered"></param>
    /// <returns></returns>
    public Expression<Func<DbContext, IEnumerable>> GetQueryExpression(Type entityType, string sqlQuery, bool ordered = true)
    {
        var parameter = Expression.Parameter(typeof(DbContext), "ctx");
        var expression = Expression.Call(parameter, "Set", new Type[] { entityType });
        expression = Expression.Call(typeof(RelationalQueryableExtensions), "FromSqlRaw", new Type[] { entityType }, expression, Expression.Constant(sqlQuery), Expression.Constant(Array.Empty<object>()));
        if (BulkConfig.TrackingEntities) // If Else can not be replaced with Ternary operator for Expression
        {
        }
        else
        {
            expression = Expression.Call(typeof(EntityFrameworkQueryableExtensions), "AsNoTracking", new Type[] { entityType }, expression);
        }
        expression = ordered ? OrderBy(entityType, expression, PrimaryKeysPropertyColumnNameDict.Select(a => a.Key).ToList()) : expression;
        return Expression.Lambda<Func<DbContext, IEnumerable>>(expression, parameter);

        // ALTERNATIVELY OrderBy with DynamicLinq ('using System.Linq.Dynamic.Core;' NuGet required) that eliminates need for custom OrderBy<T> method with Expression.
        //var queryOrdered = query.OrderBy(PrimaryKeys[0]);
    }

    private static MethodCallExpression OrderBy(Type entityType, Expression source, List<string> orderings)
    {
        var expression = (MethodCallExpression)source;
        ParameterExpression parameter = Expression.Parameter(entityType);
        bool firstArgOrderBy = true;
        foreach (var ordering in orderings)
        {
            PropertyInfo? property = entityType.GetProperty(ordering);
            if (property != null)
            {
                MemberExpression propertyAccess = Expression.MakeMemberAccess(parameter, property);
                LambdaExpression orderByExp = Expression.Lambda(propertyAccess, parameter);
                string methodName = firstArgOrderBy ? "OrderBy" : "ThenBy";
                expression = Expression.Call(typeof(Queryable), methodName, new Type[] { entityType, property.PropertyType }, expression, Expression.Quote(orderByExp));
                firstArgOrderBy = false;
            }
        }
        return expression;
    }
    #endregion

    // Currently not used until issue from previous segment is fixed in EFCore
    #region DirectQuery
    /*public void UpdateOutputIdentity<T>(DbContext context, IList<T> entities) where T : class
    {
        if (HasSinglePrimaryKey)
        {
            var entitiesWithOutputIdentity = QueryOutputTable<T>(context).ToList();
            UpdateEntitiesIdentity(entities, entitiesWithOutputIdentity);
        }
    }

    public async Task UpdateOutputIdentityAsync<T>(DbContext context, IList<T> entities) where T : class
    {
        if (HasSinglePrimaryKey)
        {
            var entitiesWithOutputIdentity = await QueryOutputTable<T>(context).ToListAsync().ConfigureAwait(false);
            UpdateEntitiesIdentity(entities, entitiesWithOutputIdentity);
        }
    }

    protected IQueryable<T> QueryOutputTable<T>(DbContext context) where T : class
    {
        string q = SqlQueryBuilder.SelectFromOutputTable(this);
        var query = context.Set<T>().FromSql(q);
        if (!BulkConfig.TrackingEntities)
        {
            query = query.AsNoTracking();
        }

        var queryOrdered = OrderBy(query, PrimaryKeys[0]);
        // ALTERNATIVELY OrderBy with DynamicLinq ('using System.Linq.Dynamic.Core;' NuGet required) that eliminates need for custom OrderBy<T> method with Expression.
        //var queryOrdered = query.OrderBy(PrimaryKeys[0]);

        return queryOrdered;
    }

    private static IQueryable<T> OrderBy<T>(IQueryable<T> source, string ordering)
    {
        Type entityType = typeof(T);
        PropertyInfo property = entityType.GetProperty(ordering);
        ParameterExpression parameter = Expression.Parameter(entityType);
        MemberExpression propertyAccess = Expression.MakeMemberAccess(parameter, property);
        LambdaExpression orderByExp = Expression.Lambda(propertyAccess, parameter);
        MethodCallExpression resultExp = Expression.Call(typeof(Queryable), "OrderBy", new Type[] { entityType, property.PropertyType }, source.Expression, Expression.Quote(orderByExp));
        var orderedQuery = source.Provider.CreateQuery<T>(resultExp);
        return orderedQuery;
    }*/
    #endregion
}
