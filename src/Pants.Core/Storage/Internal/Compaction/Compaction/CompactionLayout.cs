namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class CompactionLayout
{
    sealed class EndpointGroup
    {
        public int Count { get; set; }

        public List<int> Representatives { get; } = [];
    }

    public static FileMeta[][] RepairComponents(IReadOnlyList<FileMeta> files)
    {
        var order = Enumerable.Range(0, files.Count)
            .Where(index =>
            {
                var file = files[index];
                return file.KeyBoundsComplete && file.SmallestKey is not null &&
                       file.LargestKey is not null &&
                       ByteArrayComparer.Instance.Compare(file.SmallestKey, file.LargestKey) <= 0;
            })
            .ToArray();
        Array.Sort(order, (left, right) => CompareFiles(files[left], files[right]));

        var parents = Enumerable.Range(0, files.Count).ToArray();
        var activeEndpoints = new SortedSet<byte[]>(ByteArrayComparer.Instance);
        var activeGroups = new SortedDictionary<byte[], EndpointGroup>(ByteArrayComparer.Instance);
        var activeCount = 0;
        var found = false;
        foreach (var index in order)
        {
            var file = files[index];
            var start = file.SmallestKey!;
            var end = file.LargestKey!;
            while (activeEndpoints.Count != 0 &&
                   ByteArrayComparer.Instance.Compare(activeEndpoints.Min!, start) < 0)
            {
                var expiredEnd = activeEndpoints.Min!;
                activeEndpoints.Remove(expiredEnd);
                activeCount -= activeGroups[expiredEnd].Count;
                activeGroups.Remove(expiredEnd);
            }

            if (activeEndpoints.Count != 0)
            {
                var furthestEnd = activeEndpoints.Max!;
                if (ByteArrayComparer.Instance.Compare(furthestEnd, start) > 0)
                {
                    Connect(parents, index, activeGroups[furthestEnd].Representatives[0]);
                    found = true;
                }
            }

            if (activeCount >= 2)
            {
                if (activeGroups.TryGetValue(start, out var equalEndpoint))
                {
                    foreach (var representative in equalEndpoint.Representatives)
                    {
                        Connect(parents, index, representative);
                    }
                }

                found = true;
            }

            if (!activeGroups.TryGetValue(end, out var group))
            {
                group = new EndpointGroup();
                activeGroups.Add(end, group);
                activeEndpoints.Add(end);
            }

            group.Count++;
            activeCount++;
            if (group.Count <= 2)
            {
                group.Representatives.Add(index);
            }
            else
            {
                foreach (var representative in group.Representatives)
                {
                    Connect(parents, index, representative);
                }

                group.Representatives.Clear();
                group.Representatives.Add(index);
            }
        }

        if (!found)
        {
            return [];
        }

        var groups = new Dictionary<int, List<FileMeta>>();
        foreach (var index in order)
        {
            var root = FindRoot(parents, index);
            if (!groups.TryGetValue(root, out var component))
            {
                component = [];
                groups.Add(root, component);
            }

            component.Add(files[index]);
        }

        return groups.Values
            .Where(static component => component.Count > 1)
            .OrderBy(static component => component[0].SmallestKey!, ByteArrayComparer.Instance)
            .ThenBy(static component => component[0].Name, StringComparer.Ordinal)
            .Select(static component => component.ToArray())
            .ToArray();
    }

    static int FindRoot(int[] parents, int index)
    {
        if (parents[index] != index)
        {
            parents[index] = FindRoot(parents, parents[index]);
        }

        return parents[index];
    }

    static void Connect(int[] parents, int left, int right)
    {
        var leftRoot = FindRoot(parents, left);
        var rightRoot = FindRoot(parents, right);
        if (leftRoot != rightRoot)
        {
            parents[rightRoot] = leftRoot;
        }
    }

    static int CompareFiles(FileMeta left, FileMeta right)
    {
        var comparison = ByteArrayComparer.Instance.Compare(left.SmallestKey, right.SmallestKey);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = ByteArrayComparer.Instance.Compare(left.LargestKey, right.LargestKey);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Name, right.Name);
    }
}
