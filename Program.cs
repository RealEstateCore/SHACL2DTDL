using CommandLine;
using SHACL2DTDL.VocabularyHelper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using VDS.RDF;
using VDS.RDF.Ontology;
using VDS.RDF.Parsing;
using VDS.RDF.JsonLd;
using VDS.RDF.Shacl;
using VDS.RDF.Writing;
using DotNetRdfExtensions;
using DotNetRdfExtensions.SHACL;

namespace SHACL2DTDL
{
    class Program
    {
        public class Options
        {
            [Option('n', "no-imports", Required = false, HelpText = "Sets program to not follow owl:Imports declarations.")]
            public bool NoImports { get; set; }
            [Option('f', "file-path", Required = true, HelpText = "The path to the on-disk root ontology file to translate.", SetName = "fileOntology")]
            public string? FilePath { get; set; }
            [Option('u', "uri-path", Required = true, HelpText = "The URI of the root ontology file to translate.", SetName = "uriOntology")]
            public string? UriPath { get; set; }
            [Option('o', "outputPath", Required = true, HelpText = "The directory in which to create DTDL models.")]
            public string? OutputPath { get; set; }
            [Option('m', "merged-output", Required = false, HelpText = "Sets program to output one merged JSON-LD file for batch import into ADT.")]
            public bool MergedOutput { get; set; }
            [Option('i', "ignorefile", Required = false, HelpText = "Path to a CSV file, the first column of which lists (whole or partial) IRI:s that should be ignored by this tool and not translated into DTDL output.")]
            public string? IgnoreFile { get; set; }
            [Option('s', "ontologySource", Required = false, HelpText = "An identifier for the ontology source; will be used to generate DTMI:s per the following design, where interfaceName is the local name of a translated OWL class, and ontologyName is the last segment of the translated class's namespace: <dtmi:{ontologySource}:{ontologyName}:{interfaceName};1>.")]
            public string? OntologySource { get; set; }
        }

        // Configuration fields
        private static bool _noImports;
        private static bool _localOntology;
        private static string _ontologyPath = "";
        private static string? _outputPath;
        private static bool _mergedOutput;
        private static string? _ontologySource;

        /// <summary>
        /// The RDF graph holding the SHACL-formatted ontology upon which this tool subsequently operates.
        /// </summary>
        private static readonly OntologyGraph _ontologyGraph = new OntologyGraph();
        private static readonly ShapesGraph _shapesGraph = new ShapesGraph(_ontologyGraph);

        /// <summary>
        /// URIs that will be ignored by this tool, parsed from CSV file using -i command line option
        /// </summary>
        private static readonly HashSet<string> ignoredUris = new HashSet<string>();

        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                   .WithParsed(o =>
                   {
                       _outputPath = o.OutputPath;
                       _noImports = o.NoImports;
                       _mergedOutput = o.MergedOutput;
                       if (o.FilePath != null)
                       {
                           _localOntology = true;
                           _ontologyPath = o.FilePath;
                       }
                       else if (o.UriPath != null)
                       {
                           _localOntology = false;
                           _ontologyPath = o.UriPath;
                       }

                       // Parse ignored namespaces from ignorefile
                       if (o.IgnoreFile != null)
                       {
                           using (var reader = new StreamReader(o.IgnoreFile))
                           {
                               string ignoredNamesCsv = reader.ReadToEnd();
                               string[] lines = ignoredNamesCsv.Split(Environment.NewLine);
                               IEnumerable<string> values = lines.Select(line => line.Split(';').First());
                               ignoredUris.UnionWith(values);
                           }
                       }

                       if (o.OntologySource != null)
                       {
                           _ontologySource = o.OntologySource;
                       }
                   })
                   .WithNotParsed((errs) =>
                   {
                       Environment.Exit(1);
                   });

            // Turn off caching
            UriLoader.CacheDuration = TimeSpan.MinValue;

            // Load ontology graph from local or remote path
            Console.WriteLine($"Loading {_ontologyPath}.");
            if (_localOntology)
            {
                FileLoader.Load(_ontologyGraph, _ontologyPath);
            }
            else
            {
                UriLoader.Load(_ontologyGraph, new Uri(_ontologyPath));
            }

            // TODO: Implement (recursive) model loading over owl:imports statements

            // Execute the main logic that generates DTDL interfaces.
            GenerateInterfaces();
        }

        /// <summary>
        /// Method that traverses the sets of SHACL node shapes in the imported ontology graph and generates DTDL representations.
        /// </summary>
        private static void GenerateInterfaces()
        {
            // Working graph
            Graph dtdlModel = new Graph();

            // A whole bunch of language definitions.
            // TODO Extract all of these (often reused) node definitions into statics.

            // RDF/OWL specs
            IUriNode rdfType = dtdlModel.CreateUriNode(UriFactory.Create(RdfSpecsHelper.RdfType));

            // DTDL classes
            IUriNode dtdl_Interface = dtdlModel.CreateUriNode(DTDL.Interface);
            IUriNode dtdl_Property = dtdlModel.CreateUriNode(DTDL.Property);
            IUriNode dtdl_Relationship = dtdlModel.CreateUriNode(DTDL.Relationship);
            IUriNode dtdl_Telemetry = dtdlModel.CreateUriNode(DTDL.Telemetry);
            IUriNode dtdl_Component = dtdlModel.CreateUriNode(DTDL.Component);
            IUriNode dtdl_Enum = dtdlModel.CreateUriNode(DTDL.Enum);
            IUriNode dtdl_EnumValue = dtdlModel.CreateUriNode(DTDL.EnumValue);
            IUriNode dtdl_Map = dtdlModel.CreateUriNode(DTDL.Map);
            IUriNode dtdl_Array = dtdlModel.CreateUriNode(DTDL.Array);
            IUriNode dtdl_Initialized = dtdlModel.CreateUriNode(DTDL.Initialized);

            // DTDL properties
            IUriNode dtdl_contents = dtdlModel.CreateUriNode(DTDL.contents);
            IUriNode dtdl_name = dtdlModel.CreateUriNode(DTDL.name);
            IUriNode dtdl_displayName = dtdlModel.CreateUriNode(DTDL.displayName);
            IUriNode dtdl_description = dtdlModel.CreateUriNode(DTDL.description);
            IUriNode dtdl_properties = dtdlModel.CreateUriNode(DTDL.properties);
            IUriNode dtdl_mapKey = dtdlModel.CreateUriNode(DTDL.mapKey);
            IUriNode dtdl_mapValue = dtdlModel.CreateUriNode(DTDL.mapValue);
            IUriNode dtdl_elementSchema = dtdlModel.CreateUriNode(DTDL.elementSchema);
            IUriNode dtdl_initialValue = dtdlModel.CreateUriNode(DTDL.initialValue);

            IUriNode dtdl_extends = dtdlModel.CreateUriNode(DTDL.extends);
            IUriNode dtdl_maxMultiplicity = dtdlModel.CreateUriNode(DTDL.maxMultiplicity);
            IUriNode dtdl_minMultiplicity = dtdlModel.CreateUriNode(DTDL.minMultiplicity);
            IUriNode dtdl_target = dtdlModel.CreateUriNode(DTDL.target);
            IUriNode dtdl_schema = dtdlModel.CreateUriNode(DTDL.schema);
            IUriNode dtdl_valueSchema = dtdlModel.CreateUriNode(DTDL.valueSchema);
            IUriNode dtdl_writable = dtdlModel.CreateUriNode(DTDL.writable);

            IUriNode dtdl_enumValue = dtdlModel.CreateUriNode(DTDL.enumValue);
            IUriNode dtdl_enumValues = dtdlModel.CreateUriNode(DTDL.enumValues);

            IUriNode dtdl_string = dtdlModel.CreateUriNode(DTDL._string);

            Console.WriteLine();
            Console.WriteLine("Generating DTDL Interface declarations: ");

            // Get only explicit node shapes
            foreach(NodeShape shape in _shapesGraph.NodeShapes().Where(nodeShape => nodeShape.Node.IsClass() && !IsIgnored(nodeShape.Node))) {

                // Keeping track of which RDF properties we have already parsed on a given shape
                // This is to ensure that, e.g., properties linked via rdfs:domain don't overwrite 
                // properties found via PropertyShapes
                List<string> propertiesParsed = new List<string>();
                
                // Create Interface
                string interfaceDtmi = GetDTMI(shape.Node);
                Console.WriteLine($"\t* {interfaceDtmi}");
                IUriNode interfaceNode = dtdlModel.CreateUriNode(UriFactory.Create(interfaceDtmi));
                dtdlModel.Assert(new Triple(interfaceNode, rdfType, dtdl_Interface));

                // If there are rdfs:labels, use them for DTDL displayName
                Dictionary<string,string> displayNameMap = new();
                foreach (LiteralNode shapeLabel in shape.Node.RdfsLabels()) {
                    // Flatten possibly multiple occurences of labels with a given language tag, keep only one
                    displayNameMap[shapeLabel.Language] = shapeLabel.Value;
                }
                foreach (string shapeLabelLanguageTag in displayNameMap.Keys) {
                    // Create a displayName assertion for reach of the above labels
                    ILiteralNode dtdlDisplayNameLiteral;
                    if (shapeLabelLanguageTag == String.Empty) // Fall back to EN language if none is defined b/c DTDL validator cannot handle language @none
                        dtdlDisplayNameLiteral = dtdlModel.CreateLiteralNode(string.Concat(displayNameMap[shapeLabelLanguageTag].Take(64)),"en");
                    else
                        dtdlDisplayNameLiteral = dtdlModel.CreateLiteralNode(string.Concat(displayNameMap[shapeLabelLanguageTag].Take(64)), shapeLabelLanguageTag);
                    dtdlModel.Assert(new Triple(interfaceNode, dtdl_displayName, dtdlDisplayNameLiteral));
                }

                // If there are rdfs:comments, use them for DTDL description
                Dictionary<string,string> descriptionMap = new();
                foreach (LiteralNode shapeComment in shape.Node.RdfsComments()) {
                    // Flatten possibly multiple occurences of comments with a given language tag, keep only one
                    descriptionMap[shapeComment.Language] = shapeComment.Value;
                }
                foreach (string shapeCommentLanguageTag in descriptionMap.Keys) {
                    // Create a description assertion for reach of the above comments
                    ILiteralNode dtdlDescriptionLiteral;
                    if (shapeCommentLanguageTag == String.Empty) // Fall back to EN language if none is defined b/c DTDL validator cannot handle language @none
                        dtdlDescriptionLiteral = dtdlModel.CreateLiteralNode(string.Concat(descriptionMap[shapeCommentLanguageTag].Take(512)),"en");
                    else
                        dtdlDescriptionLiteral = dtdlModel.CreateLiteralNode(string.Concat(descriptionMap[shapeCommentLanguageTag].Take(512)), shapeCommentLanguageTag);
                    dtdlModel.Assert(new Triple(interfaceNode, dtdl_description, dtdlDescriptionLiteral));
                }

                // If the class has direct superclasses, implement DTDL extends (for at most two, see limitation in DTDL spec)
                IEnumerable<NodeShape> namedSuperClasses = shape.DirectSuperShapes.Where(superClass => !superClass.IsTopThing && !superClass.IsDeprecated);
                if (namedSuperClasses.Any())
                {
                    foreach (NodeShape superClass in namedSuperClasses.Take(2))
                    {
                        string superInterfaceDTMI = GetDTMI(superClass.Node);
                        IUriNode superInterfaceNode = dtdlModel.CreateUriNode(UriFactory.Create(superInterfaceDTMI));
                        dtdlModel.Assert(new Triple(interfaceNode, dtdl_extends, superInterfaceNode));
                    }
                }
                // If it doesn't have superclasses, implement generic name property, externalIDs, and customTags
                else {
                    // Create name property node and name
                    IBlankNode namePropertyNode = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(interfaceNode, dtdl_contents, namePropertyNode));
                    dtdlModel.Assert(new Triple(namePropertyNode, rdfType, dtdl_Property));
                    ILiteralNode namePropertyDtdlNameNode = dtdlModel.CreateLiteralNode("name");
                    dtdlModel.Assert(new Triple(namePropertyNode, dtdl_name, namePropertyDtdlNameNode));
                    // Name is string
                    IUriNode namePropertySchemaNode = dtdlModel.CreateUriNode(DTDL._string);
                    dtdlModel.Assert(new Triple(namePropertyNode, dtdl_schema, namePropertySchemaNode));
                    // Display name (of name property) is hardcoded to "name".
                    ILiteralNode namePropertyDisplayNameNode = dtdlModel.CreateLiteralNode("name", "en");
                    dtdlModel.Assert(new Triple(namePropertyNode, dtdl_displayName, namePropertyDisplayNameNode));
                    // Name is writeable
                    ILiteralNode namePropertyTrueNode = dtdlModel.CreateLiteralNode("true", new Uri(XmlSpecsHelper.XmlSchemaDataTypeBoolean));
                    dtdlModel.Assert(new Triple(namePropertyNode, dtdl_writable, namePropertyTrueNode));

                    // Create externalIds
                    IBlankNode externalIds = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(interfaceNode, dtdl_contents, externalIds));
                    dtdlModel.Assert(new Triple(externalIds, rdfType, dtdl_Property));
                    ILiteralNode externalIdsDtdlName = dtdlModel.CreateLiteralNode("externalIds");
                    dtdlModel.Assert(new Triple(externalIds, dtdl_name, externalIdsDtdlName));
                    // External ids is map
                    IBlankNode schemaNode = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(schemaNode, rdfType, dtdl_Map));
                    // Map key
                    IBlankNode schemaMapKey = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(schemaNode, dtdl_mapKey, schemaMapKey));
                    ILiteralNode schemaMapKeyName = dtdlModel.CreateLiteralNode("externalIdName");
                    dtdlModel.Assert(new Triple(schemaMapKey, dtdl_name, schemaMapKeyName));
                    IUriNode schemaMapKeySchema = dtdlModel.CreateUriNode(DTDL._string);
                    dtdlModel.Assert(new Triple(schemaMapKey, dtdl_schema, schemaMapKeySchema));
                    // Map value
                    IBlankNode schemaMapValue = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(schemaNode, dtdl_mapValue, schemaMapValue));
                    ILiteralNode schemaMapValueName = dtdlModel.CreateLiteralNode("externalIdValue");
                    dtdlModel.Assert(new Triple(schemaMapValue, dtdl_name, schemaMapValueName));
                    IUriNode schemaMapValueSchema = dtdlModel.CreateUriNode(DTDL._string);
                    dtdlModel.Assert(new Triple(schemaMapValue, dtdl_schema, schemaMapValueSchema));
                    dtdlModel.Assert(new Triple(externalIds, dtdl_schema, schemaNode));
                    // Display name of external ids is hardcoded to "External IDs".
                    ILiteralNode externalIdsDisplayName = dtdlModel.CreateLiteralNode("External IDs", "en");
                    dtdlModel.Assert(new Triple(externalIds, dtdl_displayName, externalIdsDisplayName));
                    // Name is writeable
                    ILiteralNode externalIdsTrue = dtdlModel.CreateLiteralNode("true", new Uri(XmlSpecsHelper.XmlSchemaDataTypeBoolean));
                    dtdlModel.Assert(new Triple(externalIds, dtdl_writable, externalIdsTrue));

                    // Create customTags
                    IBlankNode customTags = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(interfaceNode, dtdl_contents, customTags));
                    dtdlModel.Assert(new Triple(customTags, rdfType, dtdl_Property));
                    ILiteralNode customTagsDtdlName = dtdlModel.CreateLiteralNode("customTags");
                    dtdlModel.Assert(new Triple(customTags, dtdl_name, customTagsDtdlName));
                    // Custom tags is map
                    IBlankNode customTagsSchemaNode = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(customTagsSchemaNode, rdfType, dtdl_Map));
                    // Map key
                    IBlankNode customTagsSchemaMapKey = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(customTagsSchemaNode, dtdl_mapKey, customTagsSchemaMapKey));
                    ILiteralNode customTagsSchemaMapKeyName = dtdlModel.CreateLiteralNode("tagName");
                    dtdlModel.Assert(new Triple(customTagsSchemaMapKey, dtdl_name, customTagsSchemaMapKeyName));
                    IUriNode customTagsSchemaMapKeySchema = dtdlModel.CreateUriNode(DTDL._string);
                    dtdlModel.Assert(new Triple(customTagsSchemaMapKey, dtdl_schema, customTagsSchemaMapKeySchema));
                    // Map value
                    IBlankNode customTagsSchemaMapValue = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(customTagsSchemaNode, dtdl_mapValue, customTagsSchemaMapValue));
                    ILiteralNode customTagsSchemaMapValueName = dtdlModel.CreateLiteralNode("tagValue");
                    dtdlModel.Assert(new Triple(customTagsSchemaMapValue, dtdl_name, customTagsSchemaMapValueName));
                    IUriNode customTagsSchemaMapValueSchema = dtdlModel.CreateUriNode(DTDL._string);
                    dtdlModel.Assert(new Triple(customTagsSchemaMapValue, dtdl_schema, customTagsSchemaMapValueSchema));
                    dtdlModel.Assert(new Triple(customTags, dtdl_schema, customTagsSchemaNode));
                    // Display name of custom tags is hardcoded to "Custom Tags".
                    ILiteralNode customTagsDisplayName = dtdlModel.CreateLiteralNode("Custom Tags", "en");
                    dtdlModel.Assert(new Triple(customTags, dtdl_displayName, customTagsDisplayName));
                    // Name is writeable
                    ILiteralNode customTagsTrue = dtdlModel.CreateLiteralNode("true", new Uri(XmlSpecsHelper.XmlSchemaDataTypeBoolean));
                    dtdlModel.Assert(new Triple(customTags, dtdl_writable, customTagsTrue));
                }

                // If shape has brick:hasAssociatedTag annotation, add corresponding read-only DTDL properties
                IUriNode hasAssociatedTag = _ontologyGraph.CreateUriNode(Brick.hasAssociatedTag);
                IEnumerable<string> tags = _ontologyGraph.GetTriplesWithSubjectPredicate(shape.Node, hasAssociatedTag).Objects().UriNodes().Select(node => node.Uri.Fragment);
                bool childrenHaveTags = shape.SubShapes.Select(subShape => subShape.Node).Any(subShapeNode => _ontologyGraph.GetTriplesWithSubjectPredicate(subShapeNode, hasAssociatedTag).Any());
                if (!childrenHaveTags && tags.Any()) {
                    IBlankNode tagsNode = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(interfaceNode, dtdl_contents, tagsNode);
                    dtdlModel.Assert(tagsNode, rdfType, dtdl_Property);
                    ILiteralNode tagsNameNode = dtdlModel.CreateLiteralNode("tags");
                    dtdlModel.Assert(tagsNode, dtdl_name, tagsNameNode);

                    // Documentation properties
                    ILiteralNode tagsDisplayName = dtdlModel.CreateLiteralNode("Tags","en");
                    dtdlModel.Assert(tagsNode, dtdl_displayName, tagsDisplayName);
                    ILiteralNode tagsDescription = dtdlModel.CreateLiteralNode("Brick tags associated with this interface.","en");
                    dtdlModel.Assert(tagsNode, dtdl_description, tagsDescription);
                    
                    // Schema: array of strings
                    IBlankNode schemaNode = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(tagsNode, dtdl_schema, schemaNode);
                    dtdlModel.Assert(schemaNode, rdfType, dtdl_Array);
                    dtdlModel.Assert(schemaNode, dtdl_elementSchema, dtdl_string);

                    // Set the initial values
                    dtdlModel.Assert(tagsNode, rdfType, dtdl_Initialized);
                    foreach (string tag in tags) {
                        dtdlModel.Assert(tagsNode, dtdl_initialValue, dtdlModel.CreateLiteralNode(tag.Trim('#')));
                    }

                    // Tags are NOT writable
                    ILiteralNode falseNode = dtdlModel.CreateLiteralNode("false", new Uri(XmlSpecsHelper.XmlSchemaDataTypeBoolean));
                    dtdlModel.Assert(new Triple(tagsNode, dtdl_writable, falseNode));
                }

                // Index all property shapes on the node shape
                // HashSet with name comparer means we only store every property once, regardless of if it is mentioned multiple times in source
                HashSet<Property> processedProperties = new HashSet<Property>(new Property.PropertyNameComparer());
                foreach (PropertyShape pShape in shape.PropertyShapes.Where(pShape => pShape.Path.NodeType == NodeType.Uri)) {
                    processedProperties.Add(new Property(pShape));
                }

                // Index all RDFS properties with the shape in domain
                OntologyClass oClass = _ontologyGraph.CreateOntologyClass(shape.Node);
                foreach (OntologyProperty oProp in oClass.IsDomainOf.Where(oProp => oProp.Resource is IUriNode)) {
                    processedProperties.Add(new Property(oProp));
                }

                // Process the previously indexed properties, creating DTDL Property or Relationship objects within the content: field
                foreach (Property property in processedProperties) {
                    string propertyName = property.Name;

                    if (RelationshipIsDefinedOnParent(shape, propertyName) || (property.Target is IUriNode target && target.IsOwlDeprecated())) {
                        continue;
                    }

                    // Create an object in the target interface contents field
                    IBlankNode contentNode = dtdlModel.CreateBlankNode();
                    dtdlModel.Assert(new Triple(interfaceNode, dtdl_contents, contentNode));

                    // Assert the content name
                    ILiteralNode propertyNameNode = dtdlModel.CreateLiteralNode(propertyName);
                    dtdlModel.Assert(new Triple(contentNode, dtdl_name, propertyNameNode));

                    // If there are property labels, use them for DTDL displayName
                    Dictionary<string,string> propertyLabelMap = new();
                    foreach (LiteralNode propertyLabel in property.Labels) {
                        // Flatten possibly multiple occurences of labels with a given language tag, keep only one
                        propertyLabelMap[propertyLabel.Language] = propertyLabel.Value;
                    }
                    foreach (string propertyLabelLanguageTag in propertyLabelMap.Keys) {
                        // Create a displayName assertion for reach of the above labels
                        ILiteralNode dtdlDisplayNameLiteral;
                        if (propertyLabelLanguageTag == String.Empty) // Fall back to EN language if none is defined b/c DTDL validator cannot handle language @none
                            dtdlDisplayNameLiteral = dtdlModel.CreateLiteralNode(string.Concat(propertyLabelMap[propertyLabelLanguageTag].Take(64)),"en");
                        else
                            dtdlDisplayNameLiteral = dtdlModel.CreateLiteralNode(string.Concat(propertyLabelMap[propertyLabelLanguageTag].Take(64)), propertyLabelLanguageTag);
                        dtdlModel.Assert(new Triple(contentNode, dtdl_displayName, dtdlDisplayNameLiteral));
                    }

                    // If there are property comments, use them for DTDL description
                    Dictionary<string,string> propertyDescriptionMap = new();
                    foreach (LiteralNode propertyComment in property.Comments) {
                        // Flatten possibly multiple occurences of comments with a given language tag, keep only one
                        propertyDescriptionMap[propertyComment.Language] = propertyComment.Value;
                    }
                    foreach (string propertyCommentLanguageTag in propertyDescriptionMap.Keys) {
                        // Create a description assertion for reach of the above comments
                        ILiteralNode dtdlDescriptionLiteral;
                        if (propertyCommentLanguageTag == String.Empty) // Fall back to EN language if none is defined b/c DTDL validator cannot handle language @none
                            dtdlDescriptionLiteral = dtdlModel.CreateLiteralNode(string.Concat(propertyDescriptionMap[propertyCommentLanguageTag].Take(512)),"en");
                        else
                            dtdlDescriptionLiteral = dtdlModel.CreateLiteralNode(string.Concat(propertyDescriptionMap[propertyCommentLanguageTag].Take(512)), propertyCommentLanguageTag);
                        dtdlModel.Assert(new Triple(contentNode, dtdl_description, dtdlDescriptionLiteral));
                    }

                    // If this is a data property or if it targets a Brick value shape, we'll interpret as a DTDL property
                    if (property.Type == Property.PropertyType.Data || (property.Target != null && IsBrickValueShape(property.Target)) || property.Target != null && IsDtdlEnumeration(property.Target)) {
                        // This content node is a DTDL Property
                        dtdlModel.Assert(new Triple(contentNode, rdfType, dtdl_Property));

                        // Property is is writeable
                        ILiteralNode trueNode = dtdlModel.CreateLiteralNode("true", new Uri(XmlSpecsHelper.XmlSchemaDataTypeBoolean));
                        dtdlModel.Assert(new Triple(contentNode, dtdl_writable, trueNode));

                        INode schemaNode;
                        if (property.Type == Property.PropertyType.Data) {
                            // This is a simple data property translation
                            // If target is unset, fall back to string; else try XSD translation
                            // TODO: Handle sh:in -> DTDL enumeration translation?
                            if (property.In.Count() > 0) {
                                IUriNode dtdlSchema = dtdlModel.CreateUriNode(DTDL.schema);
                                IUriNode dtdlName = dtdlModel.CreateUriNode(DTDL.name);
                                IUriNode dtdlString = dtdlModel.CreateUriNode(DTDL._string);
                                IUriNode dtdlEnum = dtdlModel.CreateUriNode(DTDL.Enum);
                                IUriNode dtdlValueSchema = dtdlModel.CreateUriNode(DTDL.valueSchema);
                                IUriNode dtdlEnumValue = dtdlModel.CreateUriNode(DTDL.enumValue);
                                IUriNode dtdlEnumValues = dtdlModel.CreateUriNode(DTDL.enumValues);

                                IEnumerable<string> enumOptions = property.In.LiteralNodes().Select(n => n.Value);
                                IBlankNode enumNode = dtdlModel.CreateBlankNode();
                                dtdlModel.Assert(contentNode, dtdlSchema, enumNode);
                                dtdlModel.Assert(enumNode, rdfType, dtdlEnum);
                                dtdlModel.Assert(enumNode, dtdlValueSchema, dtdlString);
                                
                                foreach (string option in enumOptions)
                                {
                                    IBlankNode enumOption = dtdlModel.CreateBlankNode();
                                    char[] numbers = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                                    string sanitizedOption = Regex.Replace(option, @"[^A-Za-z0-9_]", "_").TrimStart(numbers);
                                    dtdlModel.Assert(enumOption, dtdlName, dtdlModel.CreateLiteralNode(sanitizedOption));
                                    dtdlModel.Assert(enumOption, dtdlEnumValue, dtdlModel.CreateLiteralNode(sanitizedOption));
                                    dtdlModel.Assert(enumNode, dtdlEnumValues, enumOption);
                                }
                            }
                            else {
                                Uri schema = property.Target is null ? DTDL._string : GetXsdAsDtdl(property.Target);
                                schemaNode = dtdlModel.CreateUriNode(schema);
                                dtdlModel.Assert(new Triple(contentNode, dtdl_schema, schemaNode));
                            }
                        }
                        else if (property.Target != null && IsBrickValueShape(property.Target)) {
                            // This is a Brick ValueShape translation
                            NodeShape targetShape = new NodeShape(property.Target, _shapesGraph);
                            schemaNode = AssertDtdlSchemaFromBrickValueShape(targetShape, dtdlModel);
                            dtdlModel.Assert(new Triple(contentNode, dtdl_schema, schemaNode));
                        }
                        // TODO: Break this out into a function (see also the brick value shape translation which uses the same code)
                        else if (property.Target != null && IsDtdlEnumeration(property.Target)) {
                            IUriNode dtdlSchema = dtdlModel.CreateUriNode(DTDL.schema);
                            IUriNode dtdlName = dtdlModel.CreateUriNode(DTDL.name);
                            IUriNode dtdlString = dtdlModel.CreateUriNode(DTDL._string);
                            IUriNode dtdlEnum = dtdlModel.CreateUriNode(DTDL.Enum);
                            IUriNode dtdlValueSchema = dtdlModel.CreateUriNode(DTDL.valueSchema);
                            IUriNode dtdlEnumValue = dtdlModel.CreateUriNode(DTDL.enumValue);
                            IUriNode dtdlEnumValues = dtdlModel.CreateUriNode(DTDL.enumValues);

                            IEnumerable<string> enumOptions = property.Target.SubClasses().Append(property.Target).SelectMany(subClass => subClass.RdfTypedMembers().UriNodes()).Select(optionNode => optionNode.LocalName());
                            IBlankNode enumNode = dtdlModel.CreateBlankNode();
                            dtdlModel.Assert(contentNode, dtdlSchema, enumNode);
                            dtdlModel.Assert(enumNode, rdfType, dtdlEnum);
                            dtdlModel.Assert(enumNode, dtdlValueSchema, dtdlString);
                            
                            foreach (string option in enumOptions)
                            {
                                IBlankNode enumOption = dtdlModel.CreateBlankNode();
                                char[] numbers = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                                string sanitizedOption = Regex.Replace(option, @"[^A-Za-z0-9_]", "_").TrimStart(numbers);
                                dtdlModel.Assert(enumOption, dtdlName, dtdlModel.CreateLiteralNode(sanitizedOption));
                                dtdlModel.Assert(enumOption, dtdlEnumValue, dtdlModel.CreateLiteralNode(sanitizedOption));
                                dtdlModel.Assert(enumNode, dtdlEnumValues, enumOption);
                            }
                        }
                    }
                    else if (property.Target != null && property.Target.DirectRdfTypes().Any(rdfType => rdfType.Uri.AbsoluteUri.Contains("dtmi:dtdl:class:Component"))) 
                    {
                        // Assert that this is a DTDL Component
                        dtdlModel.Assert(new Triple(contentNode, rdfType, dtdl_Component));
                        string targetDtmi = GetDTMI(property.Target);
                        IUriNode targetNode = dtdlModel.CreateUriNode(UriFactory.Create(targetDtmi));
                        dtdlModel.Assert(new Triple(contentNode, dtdl_schema, targetNode));
                    }
                    else if (property.Type == Property.PropertyType.Object) {

                        // Assert that this is a DTDL Relationship
                        dtdlModel.Assert(new Triple(contentNode, rdfType, dtdl_Relationship));

                        // Relationship is is writeable
                        ILiteralNode trueNode = dtdlModel.CreateLiteralNode("true", new Uri(XmlSpecsHelper.XmlSchemaDataTypeBoolean));
                        dtdlModel.Assert(new Triple(contentNode, dtdl_writable, trueNode));

                        // Assert the relationship target (falling back to no target if class count <> 1)
                        if (property.Target != null) {
                            string targetDtmi = GetDTMI(property.Target);
                            IUriNode targetNode = dtdlModel.CreateUriNode(UriFactory.Create(targetDtmi));
                            dtdlModel.Assert(new Triple(contentNode, dtdl_target, targetNode));
                        }

                        // Assert the cardinality
                        // Note: we ignore minMultiplicity as it is per DTDL v2 spec always 0 
                        // (see: https://github.com/Azure/opendigitaltwins-dtdl/blob/master/DTDL/v2/dtdlv2.md#relationship)
                        if (property.MaxCardinality.HasValue) {
                            ILiteralNode maxCardinality = dtdlModel.CreateLiteralNode(property.MaxCardinality.ToString(), new Uri(XmlSpecsHelper.XmlSchemaDataTypeInteger));
                            dtdlModel.Assert(contentNode, dtdl_maxMultiplicity, maxCardinality);
                        }

                        // Extract annotations on object properties -- these become DTDL Relationship Properties
                        // We only support annotations w/ singleton ranges (though those singletons may be enumerations)
                        IEnumerable<OntologyProperty> annotationsOnRelationship = _ontologyGraph.OwlAnnotationProperties
                            .Where(annotationProp => annotationProp.Resource is IUriNode)
                            .Where(annotationProp => annotationProp.Ranges.Count() == 1)
                            .Where(annotationProp => annotationProp.Domains.Select(annotationDomain => annotationDomain.Resource).Contains(property.WrappedProperty));
                        foreach (OntologyProperty annotationProperty in annotationsOnRelationship) {
                            IUriNode annotationPropertyNode = (IUriNode)annotationProperty.Resource;

                            // Define nested Property and its name
                            IBlankNode nestedPropertyNode = dtdlModel.CreateBlankNode();
                            dtdlModel.Assert(new Triple(nestedPropertyNode, rdfType, dtdl_Property));
                            dtdlModel.Assert(new Triple(contentNode, dtdl_properties, nestedPropertyNode));
                            string nestedPropertyName = string.Concat(annotationPropertyNode.LocalName().Take(64));
                            ILiteralNode nestedPropertyNameNode = dtdlModel.CreateLiteralNode(nestedPropertyName);
                            dtdlModel.Assert(new Triple(nestedPropertyNode, dtdl_name, nestedPropertyNameNode));

                            // Assert that the nested property is writable
                            dtdlModel.Assert(new Triple(nestedPropertyNode, dtdl_writable, trueNode));

                            // If there are rdfs:labels, use them for DTDL displayName
                            Dictionary<string,string> nestedDisplayNameMap = new();
                            foreach (LiteralNode propertyLabel in annotationPropertyNode.RdfsLabels()) {
                                // Flatten possibly multiple occurences of labels with a given language tag, keep only one
                                nestedDisplayNameMap[propertyLabel.Language] = propertyLabel.Value;
                            }
                            foreach (string propertyLabelLanguageTag in nestedDisplayNameMap.Keys) {
                                // Create a displayName assertion for reach of the above labels
                                ILiteralNode dtdlDisplayNameLiteral;
                                if (propertyLabelLanguageTag == String.Empty) // Fall back to EN language if none is defined b/c DTDL validator cannot handle language @none
                                    dtdlDisplayNameLiteral = dtdlModel.CreateLiteralNode(string.Concat(nestedDisplayNameMap[propertyLabelLanguageTag].Take(64)),"en");
                                else
                                    dtdlDisplayNameLiteral = dtdlModel.CreateLiteralNode(string.Concat(nestedDisplayNameMap[propertyLabelLanguageTag].Take(64)), propertyLabelLanguageTag);
                                dtdlModel.Assert(new Triple(nestedPropertyNode, dtdl_displayName, dtdlDisplayNameLiteral));
                            }

                            // If there are rdfs:comments, use them for DTDL description
                            Dictionary<string,string> nestedDescriptionMap = new();
                            foreach (LiteralNode propertyComment in annotationPropertyNode.RdfsComments()) {
                                // Flatten possibly multiple occurences of comments with a given language tag, keep only one
                                nestedDescriptionMap[propertyComment.Language] = propertyComment.Value;
                            }
                            foreach (string propertyCommentLanguageTag in nestedDescriptionMap.Keys) {
                                // Create a description assertion for reach of the above comments
                                ILiteralNode dtdlDescriptionLiteral;
                                if (propertyCommentLanguageTag == String.Empty) // Fall back to EN language if none is defined b/c DTDL validator cannot handle language @none
                                    dtdlDescriptionLiteral = dtdlModel.CreateLiteralNode(string.Concat(nestedDescriptionMap[propertyCommentLanguageTag].Take(512)),"en");
                                else
                                    dtdlDescriptionLiteral = dtdlModel.CreateLiteralNode(string.Concat(nestedDescriptionMap[propertyCommentLanguageTag].Take(512)), propertyCommentLanguageTag);
                                dtdlModel.Assert(new Triple(nestedPropertyNode, dtdl_description, dtdlDescriptionLiteral));
                            }

                            // Set range
                            OntologyClass annotationPropertyRange = annotationProperty.Ranges.First();
                            HashSet<Triple> schemaTriples = GetDtdlTriplesForRange(annotationPropertyRange, nestedPropertyNode);
                            dtdlModel.Assert(schemaTriples);
                        }
                    }
                }

                 // Do JSON-LD framing and compacting of the graph
                JObject dtdlModelAsJsonLD = ToJsonLd(dtdlModel);

                // Since the compaction algorithm and context file does not cover some edge cases,
                // we run an additional compaction using regexps to search-and-replace DTDL URNs in property keys
                JObject regexCompactedDtdlModel = RegExCompactDTDL(dtdlModelAsJsonLD);

                // Sort the contents block, if it is present, by content type and alphabetically
                JToken? contents = regexCompactedDtdlModel["contents"];
                if (contents != null && contents.Type == JTokenType.Array)
                {
                    JArray contentsArray = (JArray)contents;
                    List<JToken> sortedContents = contentsArray.OrderBy(token => token["@type"]).ThenBy(token => token["name"]).ToList();
                    regexCompactedDtdlModel["contents"] = JArray.FromObject(sortedContents);
                }

                // Sort any enums in the DTDL alphabetically
                foreach (JToken token in RecursiveChildTokens(regexCompactedDtdlModel).ToList())
                {
                    if (token is JObject && token["@type"] != null && token["@type"].ToString() == "Enum")
                    {
                        JObject enumObject = (JObject)token;
                        JToken? enumValues = enumObject["enumValues"];
                        if (enumValues != null && enumValues.Type == JTokenType.Array)
                        {
                            JArray enumValuesArray = (JArray)enumValues;
                            List<JToken> sortedEnumValues = enumValuesArray.OrderBy(valueToken => valueToken["name"]).ToList();
                            enumObject["enumValues"] = JArray.FromObject(sortedEnumValues);
                        }
                    }
                }

                List<IUriNode> parentDirectories = shape.LongestSuperShapesPath;
                string modelPath = string.Join("/", parentDirectories.Select(parent => parent.LocalName()));
                string modelOutputPath = $"{_outputPath}/{modelPath}/";
                // If the class has subclasses, place it with them
                if (shape.DirectSubShapes.Any()) { modelOutputPath += $"{shape.Node.LocalName()}/"; }
                Directory.CreateDirectory(modelOutputPath);
                string outputFileName = modelOutputPath + shape.Node.LocalName() + ".json";
                using (StreamWriter file = File.CreateText(outputFileName))
                using (JsonTextWriter writer = new JsonTextWriter(file) { Formatting = Formatting.Indented })
                {
                    regexCompactedDtdlModel.WriteTo(writer);
                }

                // Clear the working graph for next iteration
                dtdlModel.Clear();
            }

        }

        private static IEnumerable<JToken> RecursiveChildTokens(JToken root)
        {
            yield return root;
            foreach (JToken childToken in root.Children())
            {
                foreach (JToken descendantToken in RecursiveChildTokens(childToken))
                {
                    yield return descendantToken;
                }
            }
        }

        private static JObject RegExCompactDTDL(JObject dtdlModelAsJsonLD)
        {
            string input = dtdlModelAsJsonLD.ToString();
            string pattern = """
                "dtmi:dtdl:[A-Za-z0-9]*:([A-Za-z0-9]*);3":
            """;
            string replacement = "\"$1\":";
            string result = Regex.Replace(input, pattern, replacement);
            JObject retVal = JObject.Parse(result);
            return retVal;
        }

        /// <summary>
        /// Generate Digital Twin Model Identifiers; these will be based on reverse dns + path.
        /// </summary>
        /// <param name="resource">Resource to generate DTMI for</param>
        /// <returns>DTMI</returns>
        private static string GetDTMI(IUriNode resource)
        {
            // Get the resource namespace for DTMI minting (see below)
            Uri resourceNamespace = resource.GetNamespace();
            char[] uriTrimChars = { '#', '/' };

            // Combine (reversed) host and path component arrays to create namespace components array
            string[] hostComponents = resourceNamespace.Host.Split('.');
            Array.Reverse(hostComponents);
            string[] pathComponents = resourceNamespace.AbsolutePath.Trim(uriTrimChars).Split('/');
            string[] namespaceComponents = hostComponents.Concat(pathComponents).ToArray();

            // The ontologyName is the last component in the namespace array
            string ontologyName = namespaceComponents.Last();

            // If an ontology source has been set at CLI option, use it; otherwise generate from the namespace
            // components array (omitting the previously extracted ontologyName component)
            string ontologySource;
            if (_ontologySource != null)
            {
                ontologySource = _ontologySource;
            }
            else
            {
                string[] ontologySourceComponents = namespaceComponents.Take(namespaceComponents.Count() - 1).ToArray();
                ontologySource = string.Join(':', ontologySourceComponents);
            }

            // Put together the pieces
            string dtmi = $"{ontologySource}:{ontologyName}:{resource.LocalName()}";

            // Run the candidate DTMI through validation per the spec, removing non-conforming chars
            string[] pathSegments = dtmi.Split(':');
            for (int i = 0; i < pathSegments.Length; i++)
            {
                string pathSegment = pathSegments[i];
                pathSegment = new string((from c in pathSegment
                                          where char.IsLetterOrDigit(c) || c.Equals('_')
                                          select c
                                          ).ToArray());
                pathSegment = pathSegment.TrimEnd('_');
                pathSegment = pathSegment.TrimStart(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'});
                pathSegments[i] = pathSegment;
            }
            dtmi = string.Join(':', pathSegments);

            // Add prefix and suffix
            return $"dtmi:{dtmi};1";
        }

        /// <summary>
        /// Do JSON-LD framing and compacting of a model (i.e., a DTDL Interface) using the DTDL context file.
        /// </summary>
        /// <param name="dtdlModel">DTDL model to frame/compact, as DotNetRDF graph.</param>
        /// <returns>JSON-LD representation of input Interface</returns>
        private static JObject ToJsonLd(Graph dtdlModel)
        {
            JArray initialJsonLd;
            using (TripleStore entitiesStore = new TripleStore())
            {
                entitiesStore.Add(dtdlModel);
                JsonLdWriterOptions writerOptions = new JsonLdWriterOptions();
                writerOptions.UseNativeTypes = true;
                JsonLdWriter jsonLdWriter = new JsonLdWriter(writerOptions);
                initialJsonLd = jsonLdWriter.SerializeStore(entitiesStore);
            }

            // Configure and run JSON-LD framing and compacting
            JsonLdProcessorOptions options = new JsonLdProcessorOptions();
            options.UseNativeTypes = true;
            options.Base = new Uri("https://example.org"); // Throwaway base, not actually used

            JObject frame = new JObject(
                new JProperty("@type", DTDL.Interface.AbsoluteUri)
                );

            JObject context;
            using (StreamReader file = File.OpenText(@"DTDL.v3.context.json"))
            using (JsonTextReader reader = new JsonTextReader(file))
            {
                context = (JObject)JToken.ReadFrom(reader);
            }

            JObject framedJson = JsonLdProcessor.Frame(initialJsonLd, frame, options);
            JObject compactedJson = JsonLdProcessor.Compact(framedJson, context, options);

            compactedJson["@context"] = new JArray{DTDL.dtdlContext, DTDL.initializationContext};

            return compactedJson;
        }

        /// <summary>
        /// Checks if a given URI node should be ignored by this tool.
        /// </summary>
        /// <param name="uriNode">URI node to check</param>
        /// <returns>True iff the node is ignored</returns>
        private static bool IsIgnored(IUriNode uriNode)
        {
            string uri = uriNode.Uri.AbsoluteUri;
            return ignoredUris.Any(ignoredUri => uri.Contains(ignoredUri)) || uriNode.IsOwlDeprecated() || IsBrickValueShape(uriNode) || IsDtdlEnumeration(uriNode) || IsSelfTyped(uriNode);
        }

        /// <summary>
        /// Translate an XSD datatype into a DTDL URI
        /// </summary>
        /// <param name="xsdDatatype">XSD datatype to translate</param>
        /// <returns>DTDL-equivalent URI</returns>
        private static Uri GetXsdAsDtdl(IUriNode xsdDatatype)
        {
            Dictionary<string, Uri> xsdDtdlPrimitiveTypesMappings = new Dictionary<string, Uri>
                {
                    {"boolean", DTDL._boolean },
                    {"byte", DTDL._integer },
                    {"date", DTDL._date },
                    {"dateTime", DTDL._dateTime },
                    {"duration", DTDL._duration },
                    {"dateTimeStamp", DTDL._dateTime },
                    {"double", DTDL._double },
                    {"float", DTDL._float },
                    {"int", DTDL._integer },
                    {"integer", DTDL._integer },
                    {"long", DTDL._long },
                    {"string",DTDL._string },
                    {"Polygon",DTDL._polygon}
                };

            if (xsdDtdlPrimitiveTypesMappings.ContainsKey(xsdDatatype.LocalName()))
            {
                return xsdDtdlPrimitiveTypesMappings[xsdDatatype.LocalName()];
            }

            // Fall-back option
            return DTDL._string;
        }

        /// <summary>
        /// Checks whether a certain property shape on a node shape is also defined on any of its child shapes.
        /// This is necessary since DTDL does not allow sub-interfaces to extend properties from their super-interfaces.
        /// TODO Rewrite docs
        /// </summary>
        /// <param name="oClass">Superclass that holds the property being checked</param>
        /// <param name="checkedForProperty">The property to check for</param>
        /// <returns>True iff this property is not defined on any subclass</returns>
        private static bool RelationshipIsDefinedOnParent(NodeShape shape, string soughtRelationshipName)
        {
            bool propertyShapeDefinedOnParent = shape.SuperShapes.SelectMany(superShape => superShape.PropertyShapes).Select(ps => ps.Path).UriNodes().Any(pathNode => pathNode.LocalName() == soughtRelationshipName);
            bool rdfPropertyWithParentDomain = shape.SuperShapes.SelectMany(parentShape => _ontologyGraph.CreateOntologyClass(parentShape.Node).IsDomainOf).Any(property => property.Resource is IUriNode propertyNode && propertyNode.LocalName() == soughtRelationshipName);
            return propertyShapeDefinedOnParent || rdfPropertyWithParentDomain;
        }

        public static bool IsBrickValueShape(IUriNode node) {
            return node.IsNodeShape() && node.SuperClasses().Any(superClass => superClass.Uri.AbsoluteUri.Contains("https://brickschema.org/schema/BrickShape#ValueShape"));
        }

        public static bool IsDtdlEnumeration(IUriNode node) {
            return node.SubClasses().Append(node).SelectMany(subClass => subClass.RdfTypedMembers()).Any();//node.DirectSubClasses().Any() && node.DirectSubClasses().All(subClass => IsSelfTyped(subClass));
        }

        public static bool IsSelfTyped(IUriNode node) {
            IUriNode rdfType = _shapesGraph.CreateUriNode(RDF.type);
            return _shapesGraph.ContainsTriple(node, rdfType, node);
        }


 /// <summary>
        /// Generates triples representing a DTDL schema for an OWL (data) property range.
        /// </summary>
        /// <param name="owlPropertyRange">The range to translate (typically an XSD datatype or custom datatype)</param>
        /// <param name="dtdlPropertyNode">The node onto which the generated triples will be grafted</param>
        /// <returns>Set of triples representing the schema</returns>
        private static HashSet<Triple> GetDtdlTriplesForRange(OntologyClass owlPropertyRange, INode dtdlPropertyNode)
        {

            // TODO: ensure that owlPropertyRange is named!
            IGraph dtdlModel = dtdlPropertyNode.Graph;
            IUriNode dtdl_schema = dtdlModel.CreateUriNode(DTDL.schema);
            IUriNode rdfType = dtdlModel.CreateUriNode(UriFactory.Create(RdfSpecsHelper.RdfType));
            IUriNode dtdl_Enum = dtdlModel.CreateUriNode(DTDL.Enum);
            IUriNode dtdl_valueSchema = dtdlModel.CreateUriNode(DTDL.valueSchema);
            IUriNode dtdl_enumValues = dtdlModel.CreateUriNode(DTDL.enumValues);
            IUriNode dtdl_name = dtdlModel.CreateUriNode(VocabularyHelper.DTDL.name);
            IUriNode dtdl_displayName = dtdlModel.CreateUriNode(VocabularyHelper.DTDL.displayName);
            IUriNode dtdl_enumValue = dtdlModel.CreateUriNode(DTDL.enumValue);
            IUriNode dtdl_comment = dtdlModel.CreateUriNode(DTDL.comment);
            IUriNode dtdl_string = dtdlModel.CreateUriNode(DTDL._string);
            IUriNode dtdl_unit = dtdlModel.CreateUriNode(DTDL.unit);

            HashSet<Triple> returnedTriples = new HashSet<Triple>();

            // First check for the simple XSD datatypes
            if (owlPropertyRange.Resource is IUriNode && ((IUriNode)owlPropertyRange.Resource).IsXsdType())
            {
                Uri schemaUri = GetXsdAsDtdl((IUriNode)owlPropertyRange.Resource);
                IUriNode schemaNode = dtdlModel.CreateUriNode(schemaUri);
                returnedTriples.Add(new Triple(dtdlPropertyNode, dtdl_schema, schemaNode));
                return returnedTriples;
            }
            
            // This is an enumeration of allowed values
            if (owlPropertyRange.IsEnumerationDatatype())
            {
                IBlankNode enumNode = dtdlModel.CreateBlankNode();
                returnedTriples.Add(new Triple(enumNode, rdfType, dtdl_Enum));
                returnedTriples.Add(new Triple(dtdlPropertyNode, dtdl_schema, enumNode));
                returnedTriples.Add(new Triple(enumNode, dtdl_valueSchema, dtdl_string));
                IEnumerable<ILiteralNode> enumOptions = owlPropertyRange.AsEnumeration().LiteralNodes();
                foreach (ILiteralNode option in enumOptions)
                {
                    IBlankNode enumOption = dtdlModel.CreateBlankNode();
                    returnedTriples.Add(new Triple(enumOption, dtdl_name, dtdlModel.CreateLiteralNode(option.Value)));
                    returnedTriples.Add(new Triple(enumOption, dtdl_enumValue, dtdlModel.CreateLiteralNode(option.Value)));
                    returnedTriples.Add(new Triple(enumNode, dtdl_enumValues, enumOption));
                }
                return returnedTriples;
            }

            // No supported schemas found; fall back to simple string schema
            IUriNode stringSchemaNode = dtdlModel.CreateUriNode(DTDL._string);
            returnedTriples.Add(new Triple(dtdlPropertyNode, dtdl_schema, stringSchemaNode));
            return returnedTriples;
        }

        public static INode AssertDtdlSchemaFromBrickValueShape(NodeShape shape, Graph dtdlGraph) {
            IUriNode dtdlSchema = dtdlGraph.CreateUriNode(DTDL.schema);
            IUriNode rdfType = dtdlGraph.CreateUriNode(RDF.type);
            IUriNode dtdlObject = dtdlGraph.CreateUriNode(DTDL.Object);
            IUriNode dtdlFields = dtdlGraph.CreateUriNode(DTDL.fields);
            IUriNode dtdlName = dtdlGraph.CreateUriNode(DTDL.name);
            IUriNode dtdlString = dtdlGraph.CreateUriNode(DTDL._string);
            IUriNode dtdlEnum = dtdlGraph.CreateUriNode(DTDL.Enum);
            IUriNode dtdlValueSchema = dtdlGraph.CreateUriNode(DTDL.valueSchema);
            IUriNode dtdlEnumValue = dtdlGraph.CreateUriNode(DTDL.enumValue);
            IUriNode dtdlEnumValues = dtdlGraph.CreateUriNode(DTDL.enumValues);

            // Deduplicate property shape declarations on the same property (common in Brick)
            // TODO: We're keeping only the first entry, it would be more correct to build some sort of property shape
            // merge logic, but that's for a next version..
            HashSet<Property> valueShapeProperties = new HashSet<Property>(new Property.PropertyNameComparer());
            foreach (PropertyShape ps in shape.PropertyShapes) {
                valueShapeProperties.Add(new Property(ps));
            }

            // If after deduplication we have no property shapes, just return a string schema. This probably should not happen 
            // (there's not much point to a Brick ValueShape without any property shapes hanging off it..)
            if (valueShapeProperties.Count() < 1) {
                return dtdlGraph.CreateUriNode(DTDL._string);
            }
            // If after deduplication we have only one simple property shape left, coalesce into a simple schema type
            if (valueShapeProperties.Count() == 1) {
                Property valueShapeProperty = valueShapeProperties.First();
                // Falling back to string if no target is defined
                Uri targetAsXSD = valueShapeProperty.Target is not null ? GetXsdAsDtdl(valueShapeProperty.Target): DTDL._string;
                return dtdlGraph.CreateUriNode(targetAsXSD);
            }
            // Otherwise, translate all the property shapes into DTDL object fields
            else {
                IBlankNode dtdlSchemaNode = dtdlGraph.CreateBlankNode();
                dtdlGraph.Assert(dtdlSchemaNode, rdfType, dtdlObject);

                foreach (Property valueShapeProperty in valueShapeProperties) {
                    IBlankNode fieldNode = dtdlGraph.CreateBlankNode();
                    ILiteralNode fieldName = dtdlGraph.CreateLiteralNode(valueShapeProperty.WrappedProperty.LocalName());
                    dtdlGraph.Assert(dtdlSchemaNode, dtdlFields, fieldNode);
                    dtdlGraph.Assert(fieldNode, dtdlName, fieldName);

                    // If the property has an enumeration (rdf:List as rdfs:range or sh:in on SHACL shape) -> DTDL enumeration translation
                    if (valueShapeProperty.In.Count() > 0) {
                        IEnumerable<string> literalNodeEnumOptions = valueShapeProperty.In.LiteralNodes().Select(node => node.Value);
                        IEnumerable<string> uriNodeEnumOptions = valueShapeProperty.In.UriNodes().Select(node => node.LocalName());
                        IEnumerable<string> allEnumOptions = literalNodeEnumOptions.Concat(uriNodeEnumOptions);

                        IBlankNode enumNode = dtdlGraph.CreateBlankNode();
                        dtdlGraph.Assert(fieldNode, dtdlSchema, enumNode);
                        dtdlGraph.Assert(enumNode, rdfType, dtdlEnum);
                        dtdlGraph.Assert(enumNode, dtdlValueSchema, dtdlString);
                        
                        foreach (string option in allEnumOptions)
                        {
                            IBlankNode enumOption = dtdlGraph.CreateBlankNode();
                            char[] numbers = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
                            string sanitizedOption = Regex.Replace(option, @"[^A-Za-z0-9_]", "_").TrimStart(numbers);
                            dtdlGraph.Assert(enumOption, dtdlName, dtdlGraph.CreateLiteralNode(sanitizedOption));
                            dtdlGraph.Assert(enumOption, dtdlEnumValue, dtdlGraph.CreateLiteralNode(sanitizedOption));
                            dtdlGraph.Assert(enumNode, dtdlEnumValues, enumOption);
                        }
                    }
                    else {
                        // Target schema translation
                        Uri targetAsXSD = valueShapeProperty.Target is not null ? GetXsdAsDtdl(valueShapeProperty.Target): DTDL._string;
                        IUriNode fieldSchemaNode = dtdlGraph.CreateUriNode(targetAsXSD);
                        dtdlGraph.Assert(fieldNode, dtdlSchema, fieldSchemaNode);
                    }
                }
                return dtdlSchemaNode;
            }
        }
    }
}

