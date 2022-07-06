--DELETE FROM RawBlocks;
SELECT * FROM RawBlocks;
SELECT COUNT(*) FROM RawBlocks;

SELECT * FROM RawBlocks rb WHERE rb."Index" > 65500;

--UPDATE RawBlocks SET ExpandedBlockHash = null;

SELECT * FROM TwoByteMaps bm;
SELECT COUNT(*) FROM TwoByteMaps bm;
SELECT * FROM TwoByteMaps bm WHERE bm."Index" > 65500;
SELECT * FROM TwoByteMaps bm WHERE bm."Index" >= 17477;