-- Deterministic end-to-end acceptance dataset for architecture verification.
-- Runtime parameters provide the current test window, package checksum, and token hash.

INSERT INTO working_calendars (id, name, time_zone_id, calendar_json) VALUES
    ('cal-day', 'Day shift', 'UTC', $calendarDay),
    ('cal-extended', 'Extended shift', 'UTC', $calendarExtended),
    ('cal-limited', 'Limited acceptance window', 'UTC', $calendarLimited);

INSERT INTO application_settings (key, value) VALUES
    ('timeline.setup_calendar_json', $calendarExtended),
    ('acceptance.dataset', '10-cases-15-machines-v1');

INSERT INTO machines (
    id, number, name, machine_type, capabilities_json, working_calendar_id,
    status, axis_type, is_active, display_enabled) VALUES
    ('machine-01', 'M-01', 'Mill 01', 'mill', '["3-axis"]', 'cal-day', 'active', '3-axis', 1, 1),
    ('machine-02', 'M-02', 'Mill 02', 'mill', '["3-axis"]', 'cal-day', 'active', '3-axis', 1, 1),
    ('machine-03', 'M-03', 'Mill 03', 'mill', '["4-axis"]', 'cal-extended', 'active', '4-axis', 1, 1),
    ('machine-04', 'M-04', 'Mill 04', 'mill', '["5-axis"]', 'cal-extended', 'active', '5-axis', 1, 1),
    ('machine-05', 'M-05', 'Mill 05', 'mill', '["3-axis"]', 'cal-day', 'active', '3-axis', 1, 1),
    ('machine-06', 'M-06', 'Lathe 01', 'lathe', '["2-axis"]', 'cal-day', 'active', '2-axis', 1, 1),
    ('machine-07', 'M-07', 'Lathe 02', 'lathe', '["live-tooling"]', 'cal-extended', 'active', 'live-tooling', 1, 1),
    ('machine-08', 'M-08', 'Lathe 03', 'lathe', '["2-axis"]', 'cal-day', 'active', '2-axis', 1, 1),
    ('machine-09', 'M-09', 'Inspection 01', 'inspection', '["cmm"]', 'cal-day', 'active', 'cmm', 1, 1),
    ('machine-10', 'M-10', 'Inspection 02', 'inspection', '["manual"]', 'cal-day', 'active', 'manual', 1, 1),
    ('machine-11', 'M-11', 'Limited Mill', 'mill', '["3-axis"]', 'cal-limited', 'active', '3-axis', 1, 1),
    ('machine-12', 'M-12', 'Lathe 04', 'lathe', '["2-axis"]', 'cal-day', 'active', '2-axis', 1, 1),
    ('machine-13', 'M-13', 'Mill 06', 'mill', '["4-axis"]', 'cal-extended', 'active', '4-axis', 1, 1),
    ('machine-14', 'M-14', 'Lathe 05', 'lathe', '["live-tooling"]', 'cal-extended', 'active', 'live-tooling', 1, 1),
    ('machine-15', 'M-15', 'Inspection 03', 'inspection', '["cmm"]', 'cal-day', 'active', 'cmm', 1, 1);

INSERT INTO cases (
    id, part_number, revision, name, customer, customer_reference,
    working_folder_path, material_type, material_specification,
    raw_material_form, raw_material_dimensions, current_setup_seconds,
    current_cycle_seconds, notes) VALUES
    ('case-01', 'ACC-001', 'A', 'Combined order part', 'Alpha', 'A-001', 'C:\Acceptance\ACC-001', 'Aluminum', '7075-T6', 'Plate', '100x80x20', 600, 60, 'Combined batch case'),
    ('case-02', 'ACC-002', 'B', 'Split order part', 'Beta', 'B-002', 'C:\Acceptance\ACC-002', 'Steel', '4140', 'Bar', 'D50x300', 480, 90, 'Split order case'),
    ('case-03', 'ACC-003', NULL, 'Stock part', 'Internal', NULL, 'C:\Acceptance\ACC-003', 'Aluminum', '6061-T6', 'Plate', '120x90x15', 300, 45, 'Stock-only case'),
    ('case-04', 'ACC-004', 'C', 'Simultaneous part', 'Gamma', 'G-004', 'C:\Acceptance\ACC-004', 'Steel', '17-4PH', 'Billet', 'D80x150', 900, 120, 'Locked simultaneous case'),
    ('case-05', 'ACC-005', 'A', 'Lathe part five', 'Delta', NULL, 'C:\Acceptance\ACC-005', 'Steel', '4340', 'Bar', 'D40x200', 420, 75, NULL),
    ('case-06', 'ACC-006', 'A', 'Lathe part six', 'Epsilon', NULL, 'C:\Acceptance\ACC-006', 'Brass', 'C360', 'Bar', 'D30x180', 360, 55, NULL),
    ('case-07', 'ACC-007', 'A', 'Inspection part seven', 'Zeta', NULL, 'C:\Acceptance\ACC-007', 'Titanium', 'Ti-6Al-4V', 'Plate', '90x60x12', 240, 80, NULL),
    ('case-08', 'ACC-008', 'A', 'Inspection part eight', 'Eta', NULL, 'C:\Acceptance\ACC-008', 'Aluminum', '6082-T6', 'Plate', '75x55x10', 240, 50, NULL),
    ('case-09', 'ACC-009', 'A', 'Limited-calendar part', 'Theta', NULL, 'C:\Acceptance\ACC-009', 'Steel', 'A2', 'Block', '80x80x80', 1200, 600, 'Deliberate insufficient availability'),
    ('case-10', 'ACC-010', 'A', 'Missing-timing part', 'Iota', NULL, 'C:\Acceptance\ACC-010', 'Steel', '1018', 'Bar', 'D45x220', 300, 70, 'Deliberate missing timing');

INSERT INTO orders (
    id, case_id, order_reference, quantity, work_finish_date, status, notes) VALUES
    ('order-01a', 'case-01', 'SO-1001', 35, $urgentDue, 'active', 'Combined allocation A'),
    ('order-01b', 'case-01', 'SO-1002', 25, $normalDue, 'active', 'Combined allocation B'),
    ('order-02a', 'case-02', 'SO-2001', 100, $normalDue, 'active', 'Split across two batches'),
    ('order-03a', 'case-03', 'SO-3001', 10, $normalDue, 'complete', 'Historical demand; stock batch is independent'),
    ('order-04a', 'case-04', 'SO-4001', 15, $urgentDue, 'active', NULL),
    ('order-05a', 'case-05', 'SO-5001', 12, $normalDue, 'active', NULL),
    ('order-05b', 'case-05', 'SO-5002', 8, $normalDue, 'active', NULL),
    ('order-06a', 'case-06', 'SO-6001', 18, $normalDue, 'active', NULL),
    ('order-06b', 'case-06', 'SO-6002', 6, $normalDue, 'active', NULL),
    ('order-07a', 'case-07', 'SO-7001', 7, $normalDue, 'active', NULL),
    ('order-07b', 'case-07', 'SO-7002', 3, $normalDue, 'active', NULL),
    ('order-08a', 'case-08', 'SO-8001', 14, $normalDue, 'active', NULL),
    ('order-09a', 'case-09', 'SO-9001', 4, $urgentDue, 'active', NULL),
    ('order-10a', 'case-10', 'SO-10001', 9, $normalDue, 'active', NULL),
    ('order-10b', 'case-10', 'SO-10002', 1, $normalDue, 'active', NULL);

-- Two operations per Case cover every dependency type. Case 10 OP20 deliberately lacks timing.
INSERT INTO case_operations (
    id, case_id, operation_number, route_position, name, required_machine_type,
    setup_seconds, cycle_seconds, dependency_type,
    predecessor_case_operation_id, simultaneous_group_key) VALUES
    ('case-op-01a', 'case-01', 10, 0, 'Rough mill', 'mill', 600, 60, 'independent', NULL, NULL),
    ('case-op-01b', 'case-01', 20, 1, 'Finish mill', 'mill', 300, 45, 'sequential', 'case-op-01a', NULL),
    ('case-op-02a', 'case-02', 10, 0, 'Turn', 'mill', 480, 90, 'independent', NULL, NULL),
    ('case-op-02b', 'case-02', 20, 1, 'Deburr', 'mill', 120, 30, 'parallel_capable', 'case-op-02a', NULL),
    ('case-op-03a', 'case-03', 10, 0, 'Stock rough', 'mill', 300, 45, 'independent', NULL, NULL),
    ('case-op-03b', 'case-03', 20, 1, 'Stock finish', 'mill', 180, 30, 'independent', NULL, NULL),
    ('case-op-04a', 'case-04', 10, 0, 'Mill side', 'mill', 300, 120, 'locked_simultaneous', NULL, 'SIM-04'),
    ('case-op-04b', 'case-04', 20, 1, 'Turn side', 'lathe', 180, 90, 'locked_simultaneous', NULL, 'SIM-04'),
    ('case-op-05a', 'case-05', 10, 0, 'Rough turn', 'lathe', 420, 75, 'independent', NULL, NULL),
    ('case-op-05b', 'case-05', 20, 1, 'Finish turn', 'lathe', 240, 50, 'sequential', 'case-op-05a', NULL),
    ('case-op-06a', 'case-06', 10, 0, 'Turn', 'lathe', 360, 55, 'independent', NULL, NULL),
    ('case-op-06b', 'case-06', 20, 1, 'Thread', 'lathe', 240, 40, 'sequential', 'case-op-06a', NULL),
    ('case-op-07a', 'case-07', 10, 0, 'Inspect', 'inspection', 240, 80, 'independent', NULL, NULL),
    ('case-op-07b', 'case-07', 20, 1, 'Final inspect', 'inspection', 180, 45, 'sequential', 'case-op-07a', NULL),
    ('case-op-08a', 'case-08', 10, 0, 'Inspect', 'inspection', 240, 50, 'independent', NULL, NULL),
    ('case-op-08b', 'case-08', 20, 1, 'Report', 'inspection', 120, 25, 'sequential', 'case-op-08a', NULL),
    ('case-op-09a', 'case-09', 10, 0, 'Long rough', 'mill', 1200, 600, 'independent', NULL, NULL),
    ('case-op-09b', 'case-09', 20, 1, 'Long finish', 'mill', 600, 300, 'sequential', 'case-op-09a', NULL),
    ('case-op-10a', 'case-10', 10, 0, 'Timed turn', 'lathe', 300, 70, 'independent', NULL, NULL),
    ('case-op-10b', 'case-10', 20, 1, 'Unknown process', 'lathe', NULL, NULL, 'sequential', 'case-op-10a', NULL);

INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity) VALUES
    ('batch-01', 'case-01', 'B-01-COMBINED', 'waiting', 60),
    ('batch-02a', 'case-02', 'B-02-SPLIT-A', 'waiting', 40),
    ('batch-02b', 'case-02', 'B-02-SPLIT-B', 'waiting', 60),
    ('batch-03', 'case-03', 'B-03-STOCK', 'waiting', 30),
    ('batch-04', 'case-04', 'B-04-MIXED', 'waiting', 20),
    ('batch-05', 'case-05', 'B-05', 'waiting', 12),
    ('batch-06', 'case-06', 'B-06', 'waiting', 18),
    ('batch-07', 'case-07', 'B-07', 'waiting', 7),
    ('batch-08', 'case-08', 'B-08', 'waiting', 14),
    ('batch-09', 'case-09', 'B-09-CONFLICT', 'waiting', 4),
    ('batch-10', 'case-10', 'B-10-CONFLICT', 'waiting', 9);

INSERT INTO batch_allocations (
    id, production_batch_id, allocation_type, order_id, quantity) VALUES
    ('alloc-01a', 'batch-01', 'order', 'order-01a', 35),
    ('alloc-01b', 'batch-01', 'order', 'order-01b', 25),
    ('alloc-02a', 'batch-02a', 'order', 'order-02a', 40),
    ('alloc-02b', 'batch-02b', 'order', 'order-02a', 60),
    ('alloc-03-stock', 'batch-03', 'stock', NULL, 25),
    ('alloc-03-scrap', 'batch-03', 'scrap_allowance', NULL, 5),
    ('alloc-04-order', 'batch-04', 'order', 'order-04a', 15),
    ('alloc-04-stock', 'batch-04', 'stock', NULL, 4),
    ('alloc-04-scrap', 'batch-04', 'scrap_allowance', NULL, 1),
    ('alloc-05', 'batch-05', 'order', 'order-05a', 12),
    ('alloc-06', 'batch-06', 'order', 'order-06a', 18),
    ('alloc-07', 'batch-07', 'order', 'order-07a', 7),
    ('alloc-08', 'batch-08', 'order', 'order-08a', 14),
    ('alloc-09', 'batch-09', 'order', 'order-09a', 4),
    ('alloc-10', 'batch-10', 'order', 'order-10a', 9);

-- Batch Operation snapshots mirror the two Case Operations for each Batch.
INSERT INTO batch_operations (
    id, production_batch_id, source_case_operation_id, operation_number,
    route_position, name, required_machine_type, setup_seconds, cycle_seconds,
    dependency_type, predecessor_source_case_operation_id,
    simultaneous_group_key, status)
SELECT 'batch-op-' || substr(pb.id, 7) || '-a', pb.id, co.id, co.operation_number,
       co.route_position, co.name, co.required_machine_type, co.setup_seconds,
       co.cycle_seconds, co.dependency_type, co.predecessor_case_operation_id,
       co.simultaneous_group_key, 'not_started'
FROM production_batches pb
JOIN case_operations co ON co.case_id = pb.case_id AND co.route_position = 0;

INSERT INTO batch_operations (
    id, production_batch_id, source_case_operation_id, operation_number,
    route_position, name, required_machine_type, setup_seconds, cycle_seconds,
    dependency_type, predecessor_source_case_operation_id,
    simultaneous_group_key, status)
SELECT 'batch-op-' || substr(pb.id, 7) || '-b', pb.id, co.id, co.operation_number,
       co.route_position, co.name, co.required_machine_type, co.setup_seconds,
       co.cycle_seconds, co.dependency_type, co.predecessor_case_operation_id,
       co.simultaneous_group_key, 'not_started'
FROM production_batches pb
JOIN case_operations co ON co.case_id = pb.case_id AND co.route_position = 1;

-- Stable, gap-free backlogs. M-13..M-15 deliberately remain idle for dashboard coverage.
INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position) VALUES
    ('ma-01-0', 'batch-op-01-a', 'machine-01', 0),
    ('ma-01-1', 'batch-op-02a-a', 'machine-01', 1),
    ('ma-02-0', 'batch-op-01-b', 'machine-02', 0),
    ('ma-02-1', 'batch-op-02b-a', 'machine-02', 1),
    ('ma-03-0', 'batch-op-02a-b', 'machine-03', 0),
    ('ma-03-1', 'batch-op-02b-b', 'machine-03', 1),
    ('ma-04-0', 'batch-op-03-a', 'machine-04', 0),
    ('ma-04-1', 'batch-op-03-b', 'machine-04', 1),
    ('ma-05-0', 'batch-op-04-a', 'machine-05', 0),
    ('ma-06-0', 'batch-op-04-b', 'machine-06', 0),
    ('ma-07-0', 'batch-op-05-a', 'machine-07', 0),
    ('ma-07-1', 'batch-op-05-b', 'machine-07', 1),
    ('ma-08-0', 'batch-op-06-a', 'machine-08', 0),
    ('ma-08-1', 'batch-op-06-b', 'machine-08', 1),
    ('ma-09-0', 'batch-op-07-a', 'machine-09', 0),
    ('ma-09-1', 'batch-op-07-b', 'machine-09', 1),
    ('ma-10-0', 'batch-op-08-a', 'machine-10', 0),
    ('ma-10-1', 'batch-op-08-b', 'machine-10', 1),
    ('ma-11-0', 'batch-op-09-a', 'machine-11', 0),
    ('ma-11-1', 'batch-op-09-b', 'machine-11', 1),
    ('ma-12-0', 'batch-op-10-a', 'machine-12', 0),
    ('ma-12-1', 'batch-op-10-b', 'machine-12', 1);

INSERT INTO downtimes (id, machine_id, starts_at, ends_at, reason, status) VALUES
    ('down-current', 'machine-01', $downtimeCurrentStart, $downtimeCurrentEnd, 'Acceptance inspection', 'planned'),
    ('down-future', 'machine-05', $downtimeFutureStart, $downtimeFutureEnd, 'Planned maintenance', 'planned'),
    ('down-inspection', 'machine-10', $downtimeInspectionStart, $downtimeInspectionEnd, 'CMM calibration', 'planned');

INSERT INTO device_registry (
    id, device_type, device_name, machine_id, credential_hash,
    access_mode, is_enabled, metadata_json) VALUES
    ('acceptance-eink-01', 'eink', 'Acceptance Tablet 01', 'machine-01',
     $credentialHash, 'read_only', 1, '{"fixture":"acceptance"}'),
    ('acceptance-tv-01', 'tv', 'Acceptance TV 01', NULL,
     NULL, 'read_only', 1, '{"fixture":"acceptance"}');

INSERT INTO eink_package_revisions (
    id, batch_operation_id, revision, tool_cart_id, published_at,
    machine_id, machine_number, machine_name, case_id, part_number,
    part_name, part_revision, customer, production_batch_id, batch_number,
    planned_quantity, operation_number, operation_name) VALUES
    ('acceptance-package-01', 'batch-op-01-a', 'R1', 'TC-ACC-01', $publishedAt,
     'machine-01', 'M-01', 'Mill 01', 'case-01', 'ACC-001',
     'Combined order part', 'A', 'Alpha', 'batch-01', 'B-01-COMBINED',
     60, 10, 'Rough mill');

INSERT INTO eink_package_files (
    id, package_revision_id, logical_path, storage_relative_path, media_type,
    byte_length, sha256, modified_at, display_order, asset_type) VALUES
    ('acceptance-file-01', 'acceptance-package-01', 'instructions/setup.txt',
     'acceptance-package-01/setup.txt', 'text/plain; charset=utf-8',
     $packageByteLength, $packageSha256, $publishedAt, 0, 'instructions');
