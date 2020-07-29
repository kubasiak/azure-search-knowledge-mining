﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace CognitiveSearch.UI
{
    public class DocumentSearchClient
    {
        private IConfiguration _configuration { get; set; }
        private SearchIndexClient _searchIndexClient;
        private SearchClient _searchClient;
        private string searchServiceName { get; set; }
        private string apiKey { get; set; }
        private string IndexName { get; set; }
        private string IndexerName { get; set; }

        private string idField { get; set; }

        // Client logs all searches in Application Insights
        private static TelemetryClient telemetryClient = new TelemetryClient();
        public static string _searchId;

        public SearchSchema Schema { get; set; }
        public SearchModel Model { get; set; }

        public static string errorMessage;

        bool _isPathBase64Encoded { get; set; }

        // data source information. Currently supporting 3 data sources indexed by different indexers
        private static string[] containerAddresses = null;
        private static string[] tokens = null;

        // this should match the default value used in appsettings.json.  
        private static string defaultContainerUriValue = "https://{storage-account-name}.blob.core.windows.net/{container-name}";


        public DocumentSearchClient(IConfiguration configuration)
        {
            try
            {
                _configuration = configuration;
                searchServiceName = configuration.GetSection("SearchServiceName")?.Value;
                apiKey = configuration.GetSection("SearchApiKey")?.Value;
                IndexName = configuration.GetSection("SearchIndexName")?.Value;
                IndexerName = configuration.GetSection("SearchIndexerName")?.Value;
                idField = configuration.GetSection("KeyField")?.Value;
                telemetryClient.InstrumentationKey = configuration.GetSection("InstrumentationKey")?.Value;

                // Create an HTTP reference to the catalog index
                _searchIndexClient = new SearchIndexClient(new Uri($"https://{searchServiceName}.search.windows.net/"), new AzureKeyCredential(apiKey));
                _searchClient = _searchIndexClient.GetSearchClient(IndexName);

                Schema = new SearchSchema().AddFields(_searchIndexClient.GetIndex(IndexName).Value.Fields);
                Model = new SearchModel(Schema);

                _isPathBase64Encoded = (configuration.GetSection("IsPathBase64Encoded")?.Value == "True");

            }
            catch (Exception e)
            {
                // If you get an exceptio here, most likely you have not set your
                // credentials correctly in appsettings.json
                throw new ArgumentException(e.Message.ToString());
            }
        }

        public SearchResults<SearchDocument> Search(string searchText, SearchFacet[] searchFacets = null, string[] selectFilter = null, int currentPage = 1, string polygonString = null)
        {
            try
            {
                SearchOptions sp = GenerateSearchOptions(searchFacets, selectFilter, currentPage, polygonString);

                if (!string.IsNullOrEmpty(telemetryClient.InstrumentationKey))
                {
                    var s = GenerateSearchId(searchText, sp);
                    _searchId = s.Result;
                }

                return _searchClient.Search<SearchDocument>(searchText, sp);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error querying index: {0}\r\n", ex.Message.ToString());
            }
            return null;
        }

        public SearchOptions GenerateSearchOptions(SearchFacet[] searchFacets = null, string[] selectFilter = null, int currentPage = 1, string polygonString = null)
        {
            // For more information on search parameters visit: 
            // https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.search.models.SearchOptions?view=azure-dotnet
            SearchOptions sp = new SearchOptions()
            {
                SearchMode = SearchMode.All,
                Size = 10,
                Skip = (currentPage - 1) * 10,
                IncludeTotalCount = true,
                QueryType = SearchQueryType.Full,
                //Select = selectFilter,
                //Facets = Model.Facets.Select(f => f.Name).ToList(),
                //HighlightFields = Model.SearchableFields,
                HighlightPreTag = "<b>",
                HighlightPostTag = "</b>"
            };

            string filter = null;
            var filterStr = string.Empty;

            if (searchFacets != null)
            {
                foreach (var item in searchFacets)
                {
                    var facet = Model.Facets.Where(f => f.Name == item.Key).FirstOrDefault();

                    filterStr = string.Join(",", item.Value);

                    // Construct Collection(string) facet query
                    if (facet.Type == typeof(string[]))
                    {
                        if (string.IsNullOrEmpty(filter))
                            filter = $"{item.Key}/any(t: search.in(t, '{filterStr}', ','))";
                        else
                            filter += $" and {item.Key}/any(t: search.in(t, '{filterStr}', ','))";
                    }
                    // Construct string facet query
                    else if (facet.Type == typeof(string))
                    {
                        if (string.IsNullOrEmpty(filter))
                            filter = $"{item.Key} eq '{filterStr}'";
                        else
                            filter += $" and {item.Key} eq '{filterStr}'";
                    }
                    // Construct DateTime facet query
                    else if (facet.Type == typeof(DateTime))
                    {
                        // TODO: Date filters
                    }
                }
            }

            sp.Filter = filter;

            // Add Filter based on geographic polygon if it is set.
            if (polygonString != null && polygonString.Length > 0)
            {
                string geoQuery = "geo.intersects(geoLocation, geography'POLYGON((" + polygonString + "))')";
                
                if (sp.Filter != null && sp.Filter.Length > 0)
                { 
                    sp.Filter += " and " + geoQuery; 
                }
                else
                { 
                    sp.Filter = geoQuery; 
                }
            }

            return sp;
        }

        public SuggestResults<SearchDocument> Suggest(string searchText, bool fuzzy)
        {
            // Execute search based on query string
            try
            {
                SuggestOptions sp = new SuggestOptions()
                {
                    UseFuzzyMatching = fuzzy,
                    Size = 8
                };

                return _searchClient.Suggest<SearchDocument>(searchText, "sg", sp);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error querying index: {0}\r\n", ex.Message.ToString());
            }
            return null;
        }

        public AutocompleteResults Autocomplete(string searchText, bool fuzzy)
        {
            // Execute search based on query string
            try
            {
                AutocompleteOptions ap = new AutocompleteOptions()
                {
                    Mode = AutocompleteMode.OneTermWithContext,
                    UseFuzzyMatching = fuzzy,
                    Size = 8
                };

                return _searchClient.Autocomplete(searchText, "sg", ap);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error querying index: {0}\r\n", ex.Message.ToString());
            }
            return null;
        }


        public SearchDocument LookUp(string id)
        {
            // Execute geo search based on query string
            try
            {
                return _searchClient.GetDocument<SearchDocument>(id);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error querying index: {0}\r\n", ex.Message.ToString());
            }
            return null;
        }


        private async Task<string> GenerateSearchId(string searchText, SearchOptions sp)
        {
            var client = new SearchClient(new Uri($"https://{searchServiceName}.search.windows.net/"), IndexName, new AzureKeyCredential(apiKey));
            //var headers = new Dictionary<string, List<string>>() { { "x-ms-azs-return-searchid", new List<string>() { "true" } } };
            var response = await client.SearchAsync<SearchDocument>(searchText: searchText, sp);
            IEnumerable<string> headerValues;
            string searchId = string.Empty;
            if (response.GetRawResponse().Headers.TryGetValues("x-ms-azs-searchid", out headerValues))
            {
                searchId = headerValues.FirstOrDefault();
            }
            return searchId;
        }

        public string GetSearchId()
        {
            if (_searchId != null) { return _searchId; }
            return string.Empty;
        }

        public SearchResults<SearchDocument> GetFacets(string searchText, List<string> facetNames, int maxCount = 30)
        {
            var facets = new List<String>();

            foreach (var facet in facetNames) 
            {
                 facets.Add($"{facet}, count:{maxCount}");
            }

            // Execute search based on query string
            try
            {
                SearchOptions sp = new SearchOptions()
                {
                    SearchMode = SearchMode.Any,
                    Size = 10,
                    //Select = new List<String>() { idField },
                    //Facets = facets, 
                    QueryType = SearchQueryType.Full
                };

                return _searchIndexClient.GetSearchClient(IndexName).Search<SearchDocument>(searchText, sp);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error querying index: {0}\r\n", ex.Message.ToString());
            }
            return null;
        }

        public DocumentResult GetDocuments(string q, SearchFacet[] searchFacets, int currentPage, string polygonString = null)
        {
            var tokens = GetContainerSasUris();

            var selectFilter = Model.SelectFilter;

            if (!string.IsNullOrEmpty(q))
            {
                q = q.Replace("?", "");
            }

            var response = Search(q, searchFacets, selectFilter, currentPage, polygonString);
            var searchId = GetSearchId().ToString();
            var facetResults = new List<object>();
            var tagsResults = new List<object>();

            if (response != null && response.Facets != null)
            {
                // Return only the selected facets from the Search Model
                foreach (var facetResult in response.Facets.Where(f => Model.Facets.Where(x => x.Name == f.Key).Any()))
                {
                    var cleanValues = GetCleanFacetValues(facetResult);

                    facetResults.Add(new
                    {
                        key = facetResult.Key,
                        value = cleanValues
                    });
                }

                foreach (var tagResult in response.Facets.Where(t => Model.Tags.Where(x => x.Name == t.Key).Any()))
                {
                    var cleanValues = GetCleanFacetValues(tagResult);

                    tagsResults.Add(new
                    {
                        key = tagResult.Key,
                        value = cleanValues
                    });
                }
            }

            var result = new DocumentResult
            {
                Results = (response == null ? null : response.GetResults()),
                Facets = facetResults,
                Tags = tagsResults,
                Count = (response == null ? 0 : Convert.ToInt32(response.TotalCount)),
                SearchId = searchId,
                IdField = idField,
                Token = tokens[0],
                IsPathBase64Encoded = _isPathBase64Encoded
            };
            return result;
        }

        /// <summary>
        /// Initiates a run of the search indexer.
        /// </summary>
        public async Task RunIndexer()
        {
            SearchIndexerClient _searchIndexerClient = new SearchIndexerClient(new Uri($"https://{searchServiceName}.search.windows.net/"), new AzureKeyCredential(apiKey));
            var indexStatus = await _searchIndexerClient.GetIndexerStatusAsync(IndexerName);
            if (indexStatus.Value.LastResult.Status != IndexerExecutionStatus.InProgress)
            {
                _searchIndexerClient.RunIndexer(IndexerName);
            }
        }

        private string GetToken(string decodedPath, out int storageIndex)
        {
            // Initialize tokens and containers if not already initialized
            GetContainerSasUris();

            // Determine which token to use.
            string tokenToUse;
            if (decodedPath.ToLower().Contains(containerAddresses[1])) { tokenToUse = tokens[1]; storageIndex = 1; }
            else if (decodedPath.ToLower().Contains(containerAddresses[2])) { tokenToUse = tokens[2]; storageIndex = 2; }
            else { tokenToUse = tokens[0]; storageIndex = 0; }

            return tokenToUse;
        }

        /// <summary>
        /// This will return up to 3 tokens for the storage accounts
        /// </summary>
        /// <returns></returns>
        private string[] GetContainerSasUris()
        {
            // We need to refresh the tokens every time or they will become invalid.
            tokens = new string[3];
            containerAddresses = new string[3];

            string accountName = _configuration.GetSection("StorageAccountName")?.Value;
            string accountKey = _configuration.GetSection("StorageAccountKey")?.Value;
            StorageSharedKeyCredential storageSharedKeyCredential = new StorageSharedKeyCredential(accountName, accountKey);
            containerAddresses[0] = _configuration.GetSection("StorageContainerAddress")?.Value.ToLower();
            containerAddresses[1] = _configuration.GetSection("StorageContainerAddress2")?.Value.ToLower();
            containerAddresses[2] = _configuration.GetSection("StorageContainerAddress3")?.Value.ToLower();
            int containerAddressesLength = containerAddresses.Length;
            if (!String.Equals(containerAddresses[1], defaultContainerUriValue))
            {
                containerAddressesLength--;
            }
            if (!String.Equals(containerAddresses[2], defaultContainerUriValue))
            {
                containerAddressesLength--;
            }
            for (int i = 0; i < containerAddressesLength; i++) {
                BlobContainerClient container = new BlobContainerClient(new Uri(containerAddresses[i]), new StorageSharedKeyCredential(accountName, accountKey));
                var policy = new BlobSasBuilder
                {
                    Protocol = SasProtocol.HttpsAndHttp,
                    BlobContainerName = container.Name,
                    Resource = "c",
                    StartsOn = DateTimeOffset.UtcNow,
                    ExpiresOn = DateTimeOffset.UtcNow.AddHours(24),
                    IPRange = new SasIPRange(IPAddress.None, IPAddress.None)
                };
                policy.SetPermissions(BlobSasPermissions.Read);
                var sas = policy.ToSasQueryParameters(storageSharedKeyCredential).ToString();
                UriBuilder sasUri = new UriBuilder(container.Uri);
                sasUri.Query = sas;

                tokens[i] = sasUri.ToString();
            }
            return tokens;
        }

        public DocumentResult GetDocumentById(string id)
        {
            var decodedPath = id;

            var response = LookUp(id);

            if (_isPathBase64Encoded)
            {
                decodedPath = Base64Decode(id);
            }

            int storageIndex;
            string tokenToUse = GetToken(decodedPath, out storageIndex);

            var result = new DocumentResult
            {
                Result = response,
                Token = tokenToUse,
                StorageIndex = storageIndex,
                DecodedPath = decodedPath,
                IdField = idField,
                IsPathBase64Encoded = _isPathBase64Encoded
            };
            return result;
        }

        private static string Base64Decode(string input)
        {
            if (input == null) throw new ArgumentNullException("input");
            int inputLength = input.Length;
            if (inputLength < 1) return null;

            // Get padding chars
            int numPadChars = (int)input[inputLength - 1] - (int)'0';
            if (numPadChars < 0 || numPadChars > 10)
            {
                return null;
            }

            // replace '-' and '_'
            char[] base64Chars = new char[inputLength - 1 + numPadChars];
            for (int iter = 0; iter < inputLength - 1; iter++)
            {
                char c = input[iter];

                switch (c)
                {
                    case '-':
                        base64Chars[iter] = '+';
                        break;

                    case '_':
                        base64Chars[iter] = '/';
                        break;

                    default:
                        base64Chars[iter] = c;
                        break;
                }
            }

            // Add padding chars
            for (int iter = inputLength - 1; iter < base64Chars.Length; iter++)
            {
                base64Chars[iter] = '=';
            }

            var charArray = Convert.FromBase64CharArray(base64Chars, 0, base64Chars.Length);
            return System.Text.Encoding.Default.GetString(charArray);
        }

        /// <summary>
        /// In some situations you may want to restrict the facets that are displayed in the
        /// UI. This allows you to add some heuristics to remove facets that you may consider unnecessary.
        /// </summary>
        /// <param name="facetResult"></param>
        /// <returns></returns>
        private static IList<FacetResult> GetCleanFacetValues(KeyValuePair<string, IList<FacetResult>> facetResult)
        {
            IList<FacetResult> cleanValues = new List<FacetResult>();

            if (facetResult.Key == "people")
            {
                // only add names that are long enough 
                foreach (var element in facetResult.Value)
                {
                    if (element.Values.ToString().Length >= 4)
                    {
                        cleanValues.Add(element);
                    }
                }

                return cleanValues;
            }
            else
            {
                return facetResult.Value;
            }
        }
    }
}
