/* ============================================================
   StreetBiz — one-off data fix: demo slots onto the real sidewalks
   ------------------------------------------------------------
   The demo seed placed zone 1 (Nguyễn Văn Linh) as two straight rows
   whose "road" ran between them: on the real street NVL-01..10 sat
   at the north kerb but NVL-11..20 stood ~20 m inside the buildings
   north of it, and the three Hoàng Diệu slots ran diagonally across
   the block. Measured on satellite imagery against the OSM centre
   lines, every slot now stands on a sidewalk, its centre 2 m from
   the kerb: NVL-01..10 on the north side, NVL-11..20 on the south
   side (clear of the two pedestrian crossings and the alley mouth),
   HD-01/03 on the west sidewalk of Hoàng Diệu, HD-02 on the east.
   Zone 1's street features move onto the north kerb, and the three
   vendor proposals onto the north sidewalk.

   Every other street was surveyed the same way: Điện Biên Phủ and Ngô
   Quyền get one slot per side; the Bạch Đằng night-market slots, which
   stood on the Dragon Bridge over the river, move to the riverside
   promenade; Nam Kỳ Khởi Nghĩa (one row sat in the carriageway, the
   other inside the lots behind the fence), Văn Tiến Dũng (drawn at an
   angle across the lots) and Hùng Vương (four slots in the Chợ Cồn
   junction) alternate sides along the street. Hàm Nghi and Cách Mạng
   Tháng Tám keep one side only: the other sidewalk is too narrow
   (~1.5 m and ~3 m) to leave 1.5 m for pedestrians beside a slot.

   Each row moves only while it still holds the seed's original
   coordinates, so a slot an officer has placed by hand (a pilot or
   production database) is never touched. One transaction. Safe to
   run twice: the second run finds nothing to move.
     sqlcmd -S "(localdb)\MSSQLLocalDB" -d StreetBizDB -E -I -i db\StreetBiz_Upgrade_Demo_Slot_Positions.sql
   ============================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.SidewalkSlots') IS NULL OR OBJECT_ID(N'dbo.StreetFeatures') IS NULL
    THROW 50001, N'SidewalkSlots/StreetFeatures do not exist: this is not a StreetBiz database. Nothing changed.', 1;
GO

DECLARE @moved INT = 0;
BEGIN TRAN;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060932, longitude = 108.217760
     WHERE slot_code = N'NVL-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.060935 AND longitude = 108.217760;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060937, longitude = 108.217900
     WHERE slot_code = N'NVL-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.060948 AND longitude = 108.217900;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060943, longitude = 108.218040
     WHERE slot_code = N'NVL-03' AND source = 'WARD_DEFINED'
       AND latitude = 16.060961 AND longitude = 108.218040;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060949, longitude = 108.218210
     WHERE slot_code = N'NVL-04' AND source = 'WARD_DEFINED'
       AND latitude = 16.060974 AND longitude = 108.218180;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060954, longitude = 108.218320
     WHERE slot_code = N'NVL-05' AND source = 'WARD_DEFINED'
       AND latitude = 16.060987 AND longitude = 108.218320;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060958, longitude = 108.218425
     WHERE slot_code = N'NVL-06' AND source = 'WARD_DEFINED'
       AND latitude = 16.061000 AND longitude = 108.218460;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060964, longitude = 108.218600
     WHERE slot_code = N'NVL-07' AND source = 'WARD_DEFINED'
       AND latitude = 16.061013 AND longitude = 108.218600;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060970, longitude = 108.218740
     WHERE slot_code = N'NVL-08' AND source = 'WARD_DEFINED'
       AND latitude = 16.061026 AND longitude = 108.218740;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060975, longitude = 108.218880
     WHERE slot_code = N'NVL-09' AND source = 'WARD_DEFINED'
       AND latitude = 16.061039 AND longitude = 108.218880;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060980, longitude = 108.219020
     WHERE slot_code = N'NVL-10' AND source = 'WARD_DEFINED'
       AND latitude = 16.061052 AND longitude = 108.219020;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060688, longitude = 108.217760
     WHERE slot_code = N'NVL-11' AND source = 'WARD_DEFINED'
       AND latitude = 16.061277 AND longitude = 108.217760;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060696, longitude = 108.217900
     WHERE slot_code = N'NVL-12' AND source = 'WARD_DEFINED'
       AND latitude = 16.061290 AND longitude = 108.217900;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060704, longitude = 108.218040
     WHERE slot_code = N'NVL-13' AND source = 'WARD_DEFINED'
       AND latitude = 16.061303 AND longitude = 108.218040;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060714, longitude = 108.218210
     WHERE slot_code = N'NVL-14' AND source = 'WARD_DEFINED'
       AND latitude = 16.061316 AND longitude = 108.218180;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060722, longitude = 108.218358
     WHERE slot_code = N'NVL-15' AND source = 'WARD_DEFINED'
       AND latitude = 16.061329 AND longitude = 108.218320;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060726, longitude = 108.218425
     WHERE slot_code = N'NVL-16' AND source = 'WARD_DEFINED'
       AND latitude = 16.061342 AND longitude = 108.218460;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060736, longitude = 108.218600
     WHERE slot_code = N'NVL-17' AND source = 'WARD_DEFINED'
       AND latitude = 16.061355 AND longitude = 108.218600;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060744, longitude = 108.218740
     WHERE slot_code = N'NVL-18' AND source = 'WARD_DEFINED'
       AND latitude = 16.061368 AND longitude = 108.218740;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060752, longitude = 108.218880
     WHERE slot_code = N'NVL-19' AND source = 'WARD_DEFINED'
       AND latitude = 16.061381 AND longitude = 108.218880;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060760, longitude = 108.219020
     WHERE slot_code = N'NVL-20' AND source = 'WARD_DEFINED'
       AND latitude = 16.061394 AND longitude = 108.219020;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.059416, longitude = 108.217042
     WHERE slot_code = N'HD-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.059417 AND longitude = 108.217017;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.059519, longitude = 108.217144
     WHERE slot_code = N'HD-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.059537 AND longitude = 108.217157;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.059622, longitude = 108.217031
     WHERE slot_code = N'HD-03' AND source = 'WARD_DEFINED'
       AND latitude = 16.059657 AND longitude = 108.217297;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060997, longitude = 108.219460
     WHERE slot_code = N'VP-1-20260101000000001' AND source = 'VENDOR_PROPOSED' AND longitude = 108.219460
       AND latitude BETWEEN 16.061000 AND 16.061300;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060989, longitude = 108.219243
     WHERE slot_code = N'VP-1-20260101000000002' AND source = 'VENDOR_PROPOSED' AND longitude = 108.219610
       AND latitude BETWEEN 16.061000 AND 16.061300;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060942, longitude = 108.218010
     WHERE slot_code = N'VP-1-20260101000000003' AND source = 'VENDOR_PROPOSED' AND longitude = 108.218010
       AND latitude BETWEEN 16.061000 AND 16.061300;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 16.060929, longitude = 108.217960
     WHERE zone_id = 1 AND feature_type = 'TRANSFORMER' AND latitude = 16.061005 AND longitude = 108.217960;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 16.060983, longitude = 108.219360
     WHERE zone_id = 1 AND feature_type = 'HYDRANT' AND latitude = 16.061145 AND longitude = 108.219360;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 16.060956, longitude = 108.218660
     WHERE zone_id = 1 AND feature_type = 'TREE' AND latitude = 16.061075 AND longitude = 108.218660;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 16.060969, longitude = 108.219010
     WHERE zone_id = 1 AND feature_type = 'LIGHT_POLE' AND latitude = 16.061105 AND longitude = 108.219010;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 16.060998, longitude = 108.219760
     WHERE zone_id = 1 AND feature_type = 'BUS_STOP' AND latitude = 16.061185 AND longitude = 108.219760;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 16.060940, longitude = 108.218260
     WHERE zone_id = 1 AND feature_type = 'PARKING' AND latitude = 16.061035 AND longitude = 108.218260;
    SET @moved += @@ROWCOUNT;
    -- The other streets: Điện Biên Phủ, Ngô Quyền, the Bạch Đằng riverside promenade (the
    -- night-market slots stood on the Dragon Bridge, over the river), and the pilot streets
    -- Nam Kỳ Khởi Nghĩa, Văn Tiến Dũng, Hùng Vương, Hàm Nghi, Cách Mạng Tháng Tám.
    UPDATE dbo.SidewalkSlots SET latitude = 15.978078, longitude = 108.257904
     WHERE slot_code = N'NKKN-01' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258000;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977851, longitude = 108.257974
     WHERE slot_code = N'NKKN-02' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258140;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.978080, longitude = 108.258044
     WHERE slot_code = N'NKKN-03' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258280;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977854, longitude = 108.258113
     WHERE slot_code = N'NKKN-04' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258420;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.978083, longitude = 108.258183
     WHERE slot_code = N'NKKN-05' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258560;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977857, longitude = 108.258253
     WHERE slot_code = N'NKKN-06' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258700;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.978085, longitude = 108.258322
     WHERE slot_code = N'NKKN-07' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258840;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977859, longitude = 108.258392
     WHERE slot_code = N'NKKN-08' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.258980;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.978087, longitude = 108.258462
     WHERE slot_code = N'NKKN-09' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.259120;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977862, longitude = 108.258532
     WHERE slot_code = N'NKKN-10' AND source = 'WARD_DEFINED'
       AND latitude = 15.977760 AND longitude = 108.259260;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.978147, longitude = 108.261997
     WHERE slot_code = N'NKKN-11' AND source = 'WARD_DEFINED'
       AND latitude = 15.978030 AND longitude = 108.262000;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977933, longitude = 108.262137
     WHERE slot_code = N'NKKN-12' AND source = 'WARD_DEFINED'
       AND latitude = 15.978030 AND longitude = 108.262140;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.978152, longitude = 108.262276
     WHERE slot_code = N'NKKN-13' AND source = 'WARD_DEFINED'
       AND latitude = 15.978030 AND longitude = 108.262280;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977938, longitude = 108.262416
     WHERE slot_code = N'NKKN-14' AND source = 'WARD_DEFINED'
       AND latitude = 15.978030 AND longitude = 108.262420;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.978157, longitude = 108.262555
     WHERE slot_code = N'NKKN-15' AND source = 'WARD_DEFINED'
       AND latitude = 15.978030 AND longitude = 108.262560;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.977944, longitude = 108.262694
     WHERE slot_code = N'NKKN-16' AND source = 'WARD_DEFINED'
       AND latitude = 15.978030 AND longitude = 108.262700;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.061777, longitude = 108.210800
     WHERE slot_code = N'HN-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.060840 AND longitude = 108.210993;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.061643, longitude = 108.210812
     WHERE slot_code = N'HN-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.060973 AND longitude = 108.210974;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.061509, longitude = 108.210825
     WHERE slot_code = N'HN-03' AND source = 'WARD_DEFINED'
       AND latitude = 16.061107 AND longitude = 108.210955;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.061374, longitude = 108.210837
     WHERE slot_code = N'HN-04' AND source = 'WARD_DEFINED'
       AND latitude = 16.061240 AND longitude = 108.210935;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.061240, longitude = 108.210849
     WHERE slot_code = N'HN-05' AND source = 'WARD_DEFINED'
       AND latitude = 16.061374 AND longitude = 108.210916;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.061106, longitude = 108.210862
     WHERE slot_code = N'HN-06' AND source = 'WARD_DEFINED'
       AND latitude = 16.061507 AND longitude = 108.210897;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060972, longitude = 108.210874
     WHERE slot_code = N'HN-07' AND source = 'WARD_DEFINED'
       AND latitude = 16.061641 AND longitude = 108.210877;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.060838, longitude = 108.210887
     WHERE slot_code = N'HN-08' AND source = 'WARD_DEFINED'
       AND latitude = 16.061774 AND longitude = 108.210858;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067578, longitude = 108.215085
     WHERE slot_code = N'HV-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.067272 AND longitude = 108.214245;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067497, longitude = 108.215168
     WHERE slot_code = N'HV-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.067301 AND longitude = 108.214382;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067590, longitude = 108.215252
     WHERE slot_code = N'HV-03' AND source = 'WARD_DEFINED'
       AND latitude = 16.067331 AND longitude = 108.214519;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067505, longitude = 108.215335
     WHERE slot_code = N'HV-04' AND source = 'WARD_DEFINED'
       AND latitude = 16.067360 AND longitude = 108.214656;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067603, longitude = 108.215418
     WHERE slot_code = N'HV-05' AND source = 'WARD_DEFINED'
       AND latitude = 16.067389 AND longitude = 108.214793;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067513, longitude = 108.215501
     WHERE slot_code = N'HV-06' AND source = 'WARD_DEFINED'
       AND latitude = 16.067419 AND longitude = 108.214929;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067616, longitude = 108.215584
     WHERE slot_code = N'HV-07' AND source = 'WARD_DEFINED'
       AND latitude = 16.067453 AND longitude = 108.215065;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.067521, longitude = 108.215667
     WHERE slot_code = N'HV-08' AND source = 'WARD_DEFINED'
       AND latitude = 16.067479 AND longitude = 108.215203;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019540, longitude = 108.210007
     WHERE slot_code = N'CMT8-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.020155 AND longitude = 108.210859;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019564, longitude = 108.210095
     WHERE slot_code = N'CMT8-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.020074 AND longitude = 108.210747;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019589, longitude = 108.210184
     WHERE slot_code = N'CMT8-03' AND source = 'WARD_DEFINED'
       AND latitude = 16.019992 AND longitude = 108.210636;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019613, longitude = 108.210272
     WHERE slot_code = N'CMT8-04' AND source = 'WARD_DEFINED'
       AND latitude = 16.019912 AND longitude = 108.210524;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019664, longitude = 108.210457
     WHERE slot_code = N'CMT8-05' AND source = 'WARD_DEFINED'
       AND latitude = 16.019838 AND longitude = 108.210406;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019689, longitude = 108.210546
     WHERE slot_code = N'CMT8-06' AND source = 'WARD_DEFINED'
       AND latitude = 16.019770 AND longitude = 108.210286;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019713, longitude = 108.210634
     WHERE slot_code = N'CMT8-07' AND source = 'WARD_DEFINED'
       AND latitude = 16.019707 AND longitude = 108.210161;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.019738, longitude = 108.210723
     WHERE slot_code = N'CMT8-08' AND source = 'WARD_DEFINED'
       AND latitude = 16.019648 AND longitude = 108.210035;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965541, longitude = 108.259579
     WHERE slot_code = N'VTD-01' AND source = 'WARD_DEFINED'
       AND latitude = 15.964800 AND longitude = 108.260150;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965481, longitude = 108.259814
     WHERE slot_code = N'VTD-02' AND source = 'WARD_DEFINED'
       AND latitude = 15.964900 AND longitude = 108.260240;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965393, longitude = 108.259642
     WHERE slot_code = N'VTD-03' AND source = 'WARD_DEFINED'
       AND latitude = 15.965001 AND longitude = 108.260331;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965333, longitude = 108.259880
     WHERE slot_code = N'VTD-04' AND source = 'WARD_DEFINED'
       AND latitude = 15.965101 AND longitude = 108.260421;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965245, longitude = 108.259706
     WHERE slot_code = N'VTD-05' AND source = 'WARD_DEFINED'
       AND latitude = 15.965201 AND longitude = 108.260511;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965185, longitude = 108.259947
     WHERE slot_code = N'VTD-06' AND source = 'WARD_DEFINED'
       AND latitude = 15.965302 AND longitude = 108.260602;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965097, longitude = 108.259769
     WHERE slot_code = N'VTD-07' AND source = 'WARD_DEFINED'
       AND latitude = 15.965402 AND longitude = 108.260692;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.965037, longitude = 108.260013
     WHERE slot_code = N'VTD-08' AND source = 'WARD_DEFINED'
       AND latitude = 15.965502 AND longitude = 108.260782;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.964949, longitude = 108.259833
     WHERE slot_code = N'VTD-09' AND source = 'WARD_DEFINED'
       AND latitude = 15.965603 AND longitude = 108.260872;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 15.964889, longitude = 108.260079
     WHERE slot_code = N'VTD-10' AND source = 'WARD_DEFINED'
       AND latitude = 15.965703 AND longitude = 108.260963;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.066197, longitude = 108.205936
     WHERE slot_code = N'DBP-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.066229 AND longitude = 108.205945;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.065990, longitude = 108.206103
     WHERE slot_code = N'DBP-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.066369 AND longitude = 108.206105;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.080571, longitude = 108.234386
     WHERE slot_code = N'NQ-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.080457 AND longitude = 108.234506;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.080512, longitude = 108.234872
     WHERE slot_code = N'NQ-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.080597 AND longitude = 108.234666;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.062222, longitude = 108.224226
     WHERE slot_code = N'BD-EV-01' AND source = 'WARD_DEFINED'
       AND latitude = 16.061300 AND longitude = 108.226900;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.062096, longitude = 108.224291
     WHERE slot_code = N'BD-EV-02' AND source = 'WARD_DEFINED'
       AND latitude = 16.061430 AND longitude = 108.226920;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.SidewalkSlots SET latitude = 16.061970, longitude = 108.224226
     WHERE slot_code = N'BD-EV-03' AND source = 'WARD_DEFINED'
       AND latitude = 16.061560 AND longitude = 108.226940;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 15.978095, longitude = 108.259560
     WHERE feature_type = 'HYDRANT' AND latitude = 15.977760 AND longitude = 108.259560;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 15.977876, longitude = 108.258700
     WHERE feature_type = 'TREE' AND latitude = 15.977760 AND longitude = 108.258700;
    SET @moved += @@ROWCOUNT;
    UPDATE dbo.StreetFeatures SET latitude = 15.978129, longitude = 108.261569
     WHERE feature_type = 'BUS_STOP' AND latitude = 15.977894 AND longitude = 108.261569;
    SET @moved += @@ROWCOUNT;
COMMIT;
PRINT CONCAT(@moved, N' demo slot/feature row(s) moved onto the sidewalk (0 = already moved or not demo data).');
GO
