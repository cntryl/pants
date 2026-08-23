use cntryl_midge::{
    Bytes, CloudObjectLayout, ColumnFamilyHandle, Engine, MidgeError, OpenOptions, TransactionMode,
    WriteOptions,
};
use std::env;
use std::fs;
use std::path::Path;
#[cfg(feature = "failpoints")]
use std::path::PathBuf;
use std::process::ExitCode;
use std::time::Duration;

const INTEROP_COLUMN_FAMILY: &str = "interop";
const FORMAT_GOLDEN: &[u8] = b"midge-format-version=3\n";
const GOLDEN_WRITER_EPOCH: u64 = 7;
const GOLDEN_SEGMENT_ID: u64 = 11;
const GOLDEN_TRANSACTION_ID: u64 = 99;

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("Midge compatibility driver failed: {error}");
            ExitCode::FAILURE
        }
    }
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let mut arguments = env::args().skip(1);
    let command = arguments.next().ok_or("a command is required")?;

    if command == "emit-wire-goldens" {
        let output_directory =
            required_operand(arguments.next(), "an output directory is required")?;
        if arguments.next().is_some() {
            return Err("emit-wire-goldens accepts exactly one output directory".into());
        }

        return emit_wire_goldens(Path::new(&output_directory));
    }

    if command == "emit-storage-goldens" {
        let output_directory =
            required_operand(arguments.next(), "an output directory is required")?;
        if arguments.next().is_some() {
            return Err("emit-storage-goldens accepts exactly one output directory".into());
        }

        return emit_storage_goldens(Path::new(&output_directory));
    }

    let database_path = arguments.next().ok_or("a database path is required")?;
    let operand = arguments.next();
    if arguments.next().is_some() {
        return Err("too many arguments".into());
    }

    let cloud = command.starts_with("cloud-");
    let operation = command
        .strip_prefix(if cloud { "cloud-" } else { "local-" })
        .ok_or("the command must start with local- or cloud-")?;

    match operation {
        "create" | "mutate" => {
            let producer = required_operand(operand, "a producer is required")?;
            mutate_database(
                Path::new(&database_path),
                cloud,
                &producer,
                operation == "mutate",
            )
        }
        "assert" => {
            let producers = required_operand(operand, "expected producers are required")?;
            assert_database(Path::new(&database_path), cloud, &producers)
        }
        "verify" => {
            if operand.is_some() {
                return Err("verify does not accept an operand".into());
            }

            verify_database(Path::new(&database_path))
        }
        _ => Err(format!("unknown compatibility command '{command}'").into()),
    }
}

fn required_operand(
    operand: Option<String>,
    message: &'static str,
) -> Result<String, Box<dyn std::error::Error>> {
    let value = operand.ok_or(message)?;
    if value.trim().is_empty() {
        return Err(message.into());
    }

    Ok(value)
}

fn open_engine(path: &Path, cloud: bool) -> Result<Engine, MidgeError> {
    let options = if cloud {
        OpenOptions::cloud_simulated(path, "pants-compat", "database/")
    } else {
        OpenOptions::local(path)
    }
    .with_memtable_size_limit(1024 * 1024)
    .with_memtable_flush_threshold(1024 * 1024)
    .background_compaction(false)
    .build()?;

    Engine::open(options)
}

fn mutate_database(
    path: &Path,
    cloud: bool,
    producer: &str,
    flush: bool,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut engine = open_engine(path, cloud)?;
    let default_column_family = engine
        .get_column_family("default")
        .ok_or("the default column family is missing")?;
    let interop_column_family = match engine.get_column_family(INTEROP_COLUMN_FAMILY) {
        Some(column_family) => column_family,
        None => engine.create_column_family(INTEROP_COLUMN_FAMILY)?,
    };
    let write_options = if cloud {
        WriteOptions::cloud_strict()
    } else {
        WriteOptions::sync()
    };

    put_producer(
        &engine,
        &default_column_family,
        "compat",
        producer,
        write_options,
    )?;
    put_producer(
        &engine,
        &interop_column_family,
        "compat-cf",
        producer,
        write_options,
    )?;
    if flush {
        engine.flush_cf(&default_column_family)?;
        engine.flush_cf(&interop_column_family)?;
    }
    engine.shutdown(Duration::from_secs(10))?;
    Ok(())
}

fn put_producer(
    engine: &Engine,
    column_family: &ColumnFamilyHandle,
    prefix: &str,
    producer: &str,
    write_options: WriteOptions,
) -> Result<(), MidgeError> {
    let mut transaction = engine.begin_tx(column_family.id(), TransactionMode::ReadWrite)?;
    transaction.put(
        format!("{prefix}/{producer}").into_bytes(),
        format!("created-by-{producer}").into_bytes(),
        None,
    )?;
    transaction.commit(write_options)
}

fn assert_database(
    path: &Path,
    cloud: bool,
    producer_list: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut engine = open_engine(path, cloud)?;
    let default_column_family = engine
        .get_column_family("default")
        .ok_or("the default column family is missing")?;
    let interop_column_family = engine
        .get_column_family(INTEROP_COLUMN_FAMILY)
        .ok_or("the interop column family is missing")?;

    for producer in producer_list.split(',') {
        if producer.is_empty() {
            return Err("expected producers must not contain empty values".into());
        }

        assert_producer(&engine, &default_column_family, "compat", producer)?;
        assert_producer(&engine, &interop_column_family, "compat-cf", producer)?;
    }

    engine.shutdown(Duration::from_secs(10))?;
    Ok(())
}

fn assert_producer(
    engine: &Engine,
    column_family: &ColumnFamilyHandle,
    prefix: &str,
    producer: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    let transaction = engine.begin_tx(column_family.id(), TransactionMode::ReadOnly)?;
    let key = format!("{prefix}/{producer}");
    let expected = format!("created-by-{producer}").into_bytes();
    let actual = transaction
        .get(key.as_bytes())?
        .ok_or_else(|| format!("missing compatibility key '{key}'"))?;
    if actual.as_ref() != expected {
        return Err(format!("compatibility value for '{key}' did not match").into());
    }

    Ok(())
}

fn verify_database(path: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let report = Engine::verify_path(path)?;
    if !matches!(report.health, cntryl_midge::EngineHealth::Healthy) {
        return Err(format!("offline verification reported {:?}", report.health).into());
    }
    if !report.authoritative {
        return Err("offline verification report was not authoritative".into());
    }

    Ok(())
}

fn emit_wire_goldens(output_directory: &Path) -> Result<(), Box<dyn std::error::Error>> {
    let mut put_with_ttl = cntryl_midge::wal::WalRecord::new_cf(
        7,
        cntryl_midge::wal::WalOpKind::Put,
        Bytes::from_static(b"golden/put-ttl"),
        Some(Bytes::from_static(b"golden-put-value")),
        42,
        GOLDEN_WRITER_EPOCH,
    );
    put_with_ttl.expiration = Some(1_700_000_000_123);
    let insert = cntryl_midge::wal::WalRecord::new_cf(
        3,
        cntryl_midge::wal::WalOpKind::Insert,
        Bytes::from_static(b"golden/insert"),
        Some(Bytes::from_static(b"golden-insert-value")),
        43,
        GOLDEN_WRITER_EPOCH,
    );
    let delete = cntryl_midge::wal::WalRecord::new_cf(
        0,
        cntryl_midge::wal::WalOpKind::Delete,
        Bytes::from_static(b"golden/delete"),
        None,
        44,
        GOLDEN_WRITER_EPOCH,
    );
    let mut delete_range = cntryl_midge::wal::WalRecord::new_cf(
        7,
        cntryl_midge::wal::WalOpKind::DeleteRange,
        Bytes::from_static(b"golden/range/a"),
        None,
        45,
        GOLDEN_WRITER_EPOCH,
    );
    delete_range.range_end = Some(Bytes::from_static(b"golden/range/z"));
    let empty_value = cntryl_midge::wal::WalRecord::new_cf(
        0,
        cntryl_midge::wal::WalOpKind::Put,
        Bytes::from_static(b"golden/empty-value"),
        Some(Bytes::new()),
        46,
        GOLDEN_WRITER_EPOCH,
    );

    let wal_put_ttl = wal_tlv_golden(&put_with_ttl, "Put with TTL")?;
    let wal_insert = wal_tlv_golden(&insert, "Insert")?;
    let wal_delete = wal_tlv_golden(&delete, "Delete")?;
    let wal_delete_range = wal_tlv_golden(&delete_range, "DeleteRange")?;
    let wal_empty_value = wal_tlv_golden(&empty_value, "Put with an empty present value")?;
    let wal_frame = wal_frame_golden(&wal_put_ttl)?;
    let wal_transaction_batch = wal_transaction_batch_golden()?;
    let compression_input = compression_golden_input();

    let mut files = vec![
        ("FORMAT", FORMAT_GOLDEN.to_vec()),
        ("wal-tlv-put-v1.bin", wal_put_ttl),
        ("wal-tlv-insert-v1.bin", wal_insert),
        ("wal-tlv-delete-v1.bin", wal_delete),
        ("wal-tlv-delete-range-v1.bin", wal_delete_range),
        ("wal-tlv-empty-value-v1.bin", wal_empty_value),
        ("wal-frame-put-v1.bin", wal_frame),
        ("wal-txn-batch-v1.bin", wal_transaction_batch),
        ("sst-block-input-v1.bin", compression_input.clone()),
    ];
    for (file_name, algorithm) in [
        (
            "sst-block-none-v1.bin",
            cntryl_midge::sst::compression::CompressionAlgo::None,
        ),
        (
            "sst-block-lz4-v1.bin",
            cntryl_midge::sst::compression::CompressionAlgo::Lz4,
        ),
        (
            "sst-block-zstd3-v1.bin",
            cntryl_midge::sst::compression::CompressionAlgo::Zstd3,
        ),
        (
            "sst-block-zstd9-v1.bin",
            cntryl_midge::sst::compression::CompressionAlgo::Zstd9,
        ),
    ] {
        files.push((
            file_name,
            compression_block_golden(&compression_input, algorithm)?,
        ));
    }
    files.push((
        "cloud-object-keys-v1.txt",
        cloud_object_keys_golden()?.into_bytes(),
    ));

    fs::create_dir_all(output_directory).map_err(|error| {
        format!(
            "failed to create golden output directory '{}': {error}",
            output_directory.display()
        )
    })?;
    for (file_name, bytes) in files {
        let path = output_directory.join(file_name);
        fs::write(&path, bytes)
            .map_err(|error| format!("failed to write golden '{}': {error}", path.display()))?;
    }

    Ok(())
}

fn wal_tlv_golden(
    record: &cntryl_midge::wal::WalRecord,
    description: &str,
) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let encoded = cntryl_midge::wal::encoding::encode(record)?;
    let decoded = cntryl_midge::wal::encoding::decode_view(encoded.as_ref())?;
    if decoded.cf_id != record.cf_id
        || decoded.op != record.op
        || decoded.key != record.key.as_ref()
        || decoded.value != record.value.as_ref().map(Bytes::as_ref)
        || decoded.seq != record.seq
        || decoded.expiration != record.expiration
        || decoded.range_end != record.range_end.as_ref().map(Bytes::as_ref)
        || decoded.txn_id != record.txn_id
        || decoded.writer_epoch != record.writer_epoch
        || decoded.compression != record.compression
    {
        return Err(
            format!("generated {description} WAL TLV did not round-trip through Midge").into(),
        );
    }

    Ok(encoded.to_vec())
}

fn wal_frame_golden(payload: &[u8]) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let mut frame = Vec::new();
    cntryl_midge::wal::frame::append_frame(&mut frame, payload)?;
    let (payload_length, expected_crc) = cntryl_midge::wal::frame::decode_frame_header(
        &frame[..cntryl_midge::wal::frame::WAL_FRAME_HEADER_LEN],
    )?;
    let framed_payload = &frame[cntryl_midge::wal::frame::WAL_FRAME_HEADER_LEN..];
    cntryl_midge::wal::frame::verify_frame_crc(framed_payload, expected_crc)?;
    if payload_length != payload.len() || framed_payload != payload {
        return Err("generated WAL frame did not preserve its TLV payload".into());
    }
    let decoded = cntryl_midge::wal::encoding::decode_view(framed_payload)?;
    if !matches!(decoded.op, cntryl_midge::wal::WalOpKind::Put) {
        return Err("generated WAL frame did not contain a Put record".into());
    }

    Ok(frame)
}

fn wal_transaction_batch_golden() -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    const BEGIN_SEQUENCE: u64 = 100;
    const COMMIT_SEQUENCE: u64 = 103;

    let mut put = cntryl_midge::wal::WalRecord::new_cf(
        0,
        cntryl_midge::wal::WalOpKind::Put,
        Bytes::from_static(b"batch/alpha"),
        Some(Bytes::from_static(b"value-alpha")),
        101,
        GOLDEN_WRITER_EPOCH,
    );
    put.expiration = Some(1_700_000_100_456);
    put.txn_id = Some(GOLDEN_TRANSACTION_ID);

    let mut delete_range = cntryl_midge::wal::WalRecord::new_cf(
        7,
        cntryl_midge::wal::WalOpKind::DeleteRange,
        Bytes::from_static(b"batch/range/a"),
        None,
        102,
        GOLDEN_WRITER_EPOCH,
    );
    delete_range.range_end = Some(Bytes::from_static(b"batch/range/z"));
    delete_range.txn_id = Some(GOLDEN_TRANSACTION_ID);

    let records = [put, delete_range];
    let payload = cntryl_midge::wal::encoding::encode_txn_batch_payload(
        GOLDEN_TRANSACTION_ID,
        BEGIN_SEQUENCE,
        COMMIT_SEQUENCE,
        GOLDEN_WRITER_EPOCH,
        &records,
    )?;
    let mut outer_record = cntryl_midge::wal::WalRecord::new_cf(
        0,
        cntryl_midge::wal::WalOpKind::TxnBatch,
        Bytes::from_static(b"txn"),
        Some(payload.clone()),
        COMMIT_SEQUENCE,
        GOLDEN_WRITER_EPOCH,
    );
    outer_record.txn_id = Some(GOLDEN_TRANSACTION_ID);

    let encoded_outer = cntryl_midge::wal::encoding::encode(&outer_record)?;
    let decoded_outer = cntryl_midge::wal::encoding::decode(encoded_outer.clone())?;
    if decoded_outer.cf_id != outer_record.cf_id
        || decoded_outer.op != outer_record.op
        || decoded_outer.key != outer_record.key
        || decoded_outer.value != outer_record.value
        || decoded_outer.seq != outer_record.seq
        || decoded_outer.expiration != outer_record.expiration
        || decoded_outer.range_end != outer_record.range_end
        || decoded_outer.txn_id != outer_record.txn_id
        || decoded_outer.writer_epoch != outer_record.writer_epoch
        || decoded_outer.compression != outer_record.compression
    {
        return Err(
            "generated outer WAL transaction batch did not round-trip through Midge".into(),
        );
    }
    let decoded_payload = decoded_outer
        .value
        .as_ref()
        .ok_or("decoded outer WAL transaction batch did not contain a payload")?;
    let decoded = cntryl_midge::wal::encoding::decode_txn_batch_payload(
        &decoded_outer,
        decoded_payload.as_ref(),
    )?;
    if decoded.txn_id != GOLDEN_TRANSACTION_ID
        || decoded.begin_seq != BEGIN_SEQUENCE
        || decoded.commit_seq != COMMIT_SEQUENCE
        || decoded.writer_epoch != GOLDEN_WRITER_EPOCH
        || decoded.records.len() != records.len()
        || decoded.records[0].cf_id != records[0].cf_id
        || decoded.records[0].op != records[0].op
        || decoded.records[0].key != records[0].key
        || decoded.records[0].value != records[0].value
        || decoded.records[0].seq != records[0].seq
        || decoded.records[0].expiration != records[0].expiration
        || decoded.records[1].cf_id != records[1].cf_id
        || decoded.records[1].op != records[1].op
        || decoded.records[1].key != records[1].key
        || decoded.records[1].range_end != records[1].range_end
        || decoded.records[1].seq != records[1].seq
    {
        return Err("generated WAL transaction batch did not round-trip through Midge".into());
    }

    Ok(encoded_outer.to_vec())
}

fn compression_golden_input() -> Vec<u8> {
    const PATTERN: &[u8] = b"account=0042|region=east|status=active|segment=business|";
    PATTERN.iter().copied().cycle().take(16 * 1024).collect()
}

fn compression_block_golden(
    input: &[u8],
    algorithm: cntryl_midge::sst::compression::CompressionAlgo,
) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let encoded = cntryl_midge::sst::compression::compress_block_with_trailer(
        input,
        &cntryl_midge::sst::compression::CompressionPolicy::Fixed(algorithm),
    )?;
    if encoded.len() < cntryl_midge::sst::compression::BLOCK_TRAILER_SIZE {
        return Err("generated compression block was shorter than its trailer".into());
    }
    let algorithm_offset = encoded.len() - cntryl_midge::sst::compression::BLOCK_TRAILER_SIZE;
    if encoded[algorithm_offset] != algorithm.to_u8() {
        return Err(format!(
            "generated compression block used algorithm {} instead of {}",
            encoded[algorithm_offset],
            algorithm.to_u8()
        )
        .into());
    }
    let decoded = cntryl_midge::sst::compression::decompress_block_with_trailer(encoded.as_ref())?;
    if decoded.as_ref() != input {
        return Err("generated compression block did not round-trip through Midge".into());
    }

    Ok(encoded.to_vec())
}

fn cloud_object_keys_golden() -> Result<String, Box<dyn std::error::Error>> {
    let wal_segment_file_name = cntryl_midge::wal::segment_file_name(GOLDEN_SEGMENT_ID);
    let wal_segment_object_key =
        cntryl_midge::wal::segment_object_key(GOLDEN_SEGMENT_ID, GOLDEN_WRITER_EPOCH);
    let sst_file_name = cntryl_midge::sst::file_name(7, 2, 42);
    let sst_object_key = cntryl_midge::sst::object_key(&sst_file_name);
    let sst_temp_object_key = cntryl_midge::sst::temp_object_key(&sst_file_name);

    if wal_segment_file_name != "00000000000000000011.wal"
        || wal_segment_object_key != "wal/epochs/00000000000000000007/00000000000000000011.wal"
        || sst_file_name != "000007_02_00000000000000000042.sst"
        || sst_object_key != "sst/000007_02_00000000000000000042.sst"
        || sst_temp_object_key != "sst/000007_02_00000000000000000042.sst.tmp"
    {
        return Err("Midge canonical cloud key helpers produced unexpected output".into());
    }

    let metadata_key =
        |file_name: &str| format!("{}{file_name}", CloudObjectLayout::METADATA_PREFIX);
    Ok([
        format!("wal_prefix={}", CloudObjectLayout::WAL_PREFIX),
        format!(
            "wal_catalog_object_key={}",
            CloudObjectLayout::WAL_CATALOG_OBJECT_KEY
        ),
        format!("wal_segment_file_name={wal_segment_file_name}"),
        format!("wal_segment_object_key={wal_segment_object_key}"),
        format!("sst_prefix={}", CloudObjectLayout::SST_PREFIX),
        format!("sst_file_name={sst_file_name}"),
        format!("sst_object_key={sst_object_key}"),
        format!("sst_temp_object_key={sst_temp_object_key}"),
        format!("metadata_prefix={}", CloudObjectLayout::METADATA_PREFIX),
        format!("metadata_format_object_key={}", metadata_key("FORMAT")),
        format!(
            "metadata_manifest_snapshot_object_key={}",
            metadata_key("manifest.snapshot.json")
        ),
        format!(
            "metadata_manifest_object_key={}",
            metadata_key("manifest.json")
        ),
        format!(
            "metadata_manifest_journal_object_key={}",
            metadata_key("manifest.journal")
        ),
        format!(
            "metadata_intent_log_object_key={}",
            metadata_key("intent_log.json")
        ),
        format!(
            "metadata_ddl_registry_object_key={}",
            metadata_key("ddl.registry.json")
        ),
        format!("lease_object_key={}", CloudObjectLayout::LEASE_OBJECT_KEY),
    ]
    .join("\n")
        + "\n")
}

#[cfg(not(feature = "failpoints"))]
fn emit_storage_goldens(_output_directory: &Path) -> Result<(), Box<dyn std::error::Error>> {
    Err("emit-storage-goldens requires the Midge 'failpoints' feature".into())
}

#[cfg(feature = "failpoints")]
fn emit_storage_goldens(output_directory: &Path) -> Result<(), Box<dyn std::error::Error>> {
    prepare_empty_directory(output_directory)?;
    let work_directory = tempfile::tempdir()?;

    emit_active_wal_golden(work_directory.path(), output_directory)?;
    emit_cloud_wal_and_lease_goldens(work_directory.path(), output_directory)?;
    emit_structured_database_goldens(work_directory.path(), output_directory)?;
    emit_intent_log_golden(work_directory.path(), output_directory)?;
    emit_ddl_goldens(work_directory.path(), output_directory)?;

    Ok(())
}

#[cfg(feature = "failpoints")]
fn emit_active_wal_golden(
    work_directory: &Path,
    output_directory: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    let database_path = work_directory.join("active-wal-db");
    let mut engine = open_storage_golden_local_engine(&database_path)?;
    let column_family = default_column_family(&engine)?;
    commit_put(
        &engine,
        &column_family,
        b"golden/active-wal",
        b"active-wal-value",
        WriteOptions::sync(),
    )?;

    let source = database_path
        .join("wal")
        .join(cntryl_midge::wal::ACTIVE_FILE_NAME);
    let bytes = read_required_file(&source)?;
    let (_, writer_epoch, records) = validate_wal_frames(&bytes)?;
    if writer_epoch != 1 || records == 0 {
        return Err(format!(
            "active WAL used writer epoch {writer_epoch} and contained {records} records"
        )
        .into());
    }
    write_output_file(
        output_directory.join("wal/active/wal.log"),
        bytes.as_slice(),
    )?;

    engine.shutdown(Duration::from_secs(10))?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn emit_cloud_wal_and_lease_goldens(
    work_directory: &Path,
    output_directory: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    let database_path = work_directory.join("cloud-wal-db");
    let mut engine = open_storage_golden_cloud_engine(&database_path)?;
    let column_family = default_column_family(&engine)?;
    commit_put(
        &engine,
        &column_family,
        b"golden/cloud-wal",
        b"cloud-wal-value",
        WriteOptions::cloud_strict(),
    )?;

    let epoch_width = cntryl_midge::wal::WAL_SEGMENT_ID_WIDTH;
    let epoch_directory = format!("{writer_epoch:0epoch_width$}", writer_epoch = 1);
    let segment_name = cntryl_midge::wal::segment_file_name(1);
    let segment_source = database_path
        .join("cloud_store/wal/epochs")
        .join(epoch_directory)
        .join(&segment_name);
    let segment_bytes = read_required_file(&segment_source)?;
    let (max_sequence, writer_epoch, records) = validate_wal_frames(&segment_bytes)?;
    if writer_epoch != 1 || records == 0 {
        return Err(format!(
            "sealed cloud WAL used writer epoch {writer_epoch} and contained {records} records"
        )
        .into());
    }

    let catalog_bytes = read_required_file(
        &database_path
            .join("cloud_store")
            .join(CloudObjectLayout::WAL_CATALOG_OBJECT_KEY),
    )?;
    validate_publication_catalog(&catalog_bytes, &segment_bytes, max_sequence, writer_epoch)?;

    let lease_bytes = read_required_file(&database_path.join(CloudObjectLayout::LEASE_OBJECT_KEY))?;
    validate_cloud_lease(&lease_bytes)?;

    write_output_file(
        output_directory.join("wal/sealed").join(segment_name),
        &segment_bytes,
    )?;
    write_output_file(
        output_directory.join("cloud/publication-catalog.v1.json"),
        &catalog_bytes,
    )?;
    write_output_file(
        output_directory.join("leases/cloud.midge_primary_lease"),
        &lease_bytes,
    )?;

    engine.shutdown(Duration::from_secs(10))?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn emit_structured_database_goldens(
    work_directory: &Path,
    output_directory: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    const SNAPSHOT_FAILPOINT: &str =
        "midge::manifest::after_snapshot_rename_before_journal_truncate";

    let database_path = work_directory.join("structured-db");
    let mut engine = open_storage_golden_local_engine(&database_path)?;
    let column_family = default_column_family(&engine)?;
    let local_lease = read_required_file(&database_path.join(".midge_leader"))?;
    validate_local_lease(&local_lease)?;
    seed_structured_entries(&engine, &column_family)?;

    let scenario = fail::FailScenario::setup();
    fail::cfg(SNAPSHOT_FAILPOINT, "return")?;
    let flush_result = engine.flush_cf(&column_family);

    let manifest_snapshot = read_required_file(&database_path.join("manifest.snapshot.json"))?;
    validate_json_object(&manifest_snapshot, "manifest snapshot")?;
    let manifest_journal = read_required_file(&database_path.join("manifest.journal"))?;
    if manifest_journal.is_empty() {
        fail::remove(SNAPSHOT_FAILPOINT);
        scenario.teardown();
        return Err("snapshot failpoint did not retain a nonempty manifest journal".into());
    }

    let sst_path = single_file_with_extension(&database_path.join("sst"), "sst")?;
    validate_structured_sst(&sst_path)?;
    let sst_bytes = read_required_file(&sst_path)?;

    fail::remove(SNAPSHOT_FAILPOINT);
    scenario.teardown();
    flush_result.map_err(|error| {
        format!("structured SST flush failed before its checkpoint boundary: {error}")
    })?;

    write_output_file(output_directory.join("sst/structured-v4.sst"), &sst_bytes)?;
    write_output_file(
        output_directory.join("metadata/manifest.snapshot.json"),
        &manifest_snapshot,
    )?;
    write_output_file(
        output_directory.join("metadata/manifest.journal"),
        &manifest_journal,
    )?;
    write_output_file(
        output_directory.join("leases/local.midge_leader"),
        &local_lease,
    )?;

    engine.shutdown(Duration::from_secs(10))?;
    let mut recovered = open_storage_golden_local_engine(&database_path)?;
    assert_structured_visibility(&recovered, &column_family)?;
    recovered.shutdown(Duration::from_secs(10))?;
    verify_database(&database_path)?;
    fs::remove_file(database_path.join(".midge_leader"))?;
    // The nonempty journal golden above intentionally preserves Midge's
    // timestamp-bearing fsync marker. The complete database fixture publishes
    // the equally valid post-checkpoint state so every remaining byte is exact.
    fs::write(database_path.join("manifest.journal"), [])?;
    verify_database(&database_path)?;

    let database_output = output_directory.join("databases/midge-structured-v4-db");
    copy_directory(&database_path, &database_output)?;
    verify_database(&database_output)?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn emit_intent_log_golden(
    work_directory: &Path,
    output_directory: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    const INTENT_FAILPOINT: &str = "midge::manifest::inject_no_space_on_add_sst_edit";

    let database_path = work_directory.join("intent-db");
    let mut engine = open_storage_golden_local_engine(&database_path)?;
    let column_family = default_column_family(&engine)?;
    seed_numbered_entries(&engine, &column_family, "golden/intent", 16)?;

    let scenario = fail::FailScenario::setup();
    fail::cfg(INTENT_FAILPOINT, "return")?;
    let flush_error = match engine.flush_cf(&column_family) {
        Err(error) => error,
        Ok(()) => {
            fail::remove(INTENT_FAILPOINT);
            scenario.teardown();
            return Err("intent golden failpoint did not reject manifest publication".into());
        }
    };
    let intent_bytes = read_required_file(&database_path.join("intent_log.json"))?;
    validate_nonempty_json_array(&intent_bytes, "intent log")?;
    fail::remove(INTENT_FAILPOINT);
    scenario.teardown();

    if !matches!(flush_error, MidgeError::NoSpace(_)) {
        return Err(format!("intent failpoint returned unexpected error: {flush_error}").into());
    }
    write_output_file(
        output_directory.join("metadata/intent_log.json"),
        &intent_bytes,
    )?;

    engine.shutdown(Duration::from_secs(10))?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn emit_ddl_goldens(
    work_directory: &Path,
    output_directory: &Path,
) -> Result<(), Box<dyn std::error::Error>> {
    const DDL_FAILPOINT: &str = "midge::ddl::before_local_commit";

    let database_path = work_directory.join("ddl-db");
    let mut engine = open_storage_golden_cloud_engine(&database_path)?;

    let scenario = fail::FailScenario::setup();
    fail::cfg(DDL_FAILPOINT, "return")?;
    let column_family = match engine.create_column_family("fixture") {
        Ok(column_family) => column_family,
        Err(error) => {
            fail::remove(DDL_FAILPOINT);
            scenario.teardown();
            return Err(format!(
                "DDL local-commit failpoint did not preserve remote authority: {error}"
            )
            .into());
        }
    };
    if column_family.name() != "fixture" {
        fail::remove(DDL_FAILPOINT);
        scenario.teardown();
        return Err("DDL local-commit failpoint returned an unexpected column family".into());
    }
    let registry_bytes =
        read_required_file(&database_path.join("cloud_store/metadata/ddl.registry.json"))?;
    let prepare_bytes = read_required_file(&database_path.join("ddl.prepare.json"))?;
    validate_ddl_pair(&registry_bytes, &prepare_bytes)?;
    fail::remove(DDL_FAILPOINT);
    scenario.teardown();

    write_output_file(
        output_directory.join("ddl/ddl.registry.json"),
        &registry_bytes,
    )?;
    write_output_file(
        output_directory.join("ddl/ddl.prepare.json"),
        &prepare_bytes,
    )?;

    engine.shutdown(Duration::from_secs(10))?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn open_storage_golden_local_engine(path: &Path) -> Result<Engine, MidgeError> {
    Engine::open(
        OpenOptions::local(path)
            .with_memtable_size_limit(1024 * 1024)
            .with_memtable_flush_threshold(1024 * 1024)
            .background_compaction(false)
            .build()?,
    )
}

#[cfg(feature = "failpoints")]
fn open_storage_golden_cloud_engine(path: &Path) -> Result<Engine, MidgeError> {
    Engine::open(
        OpenOptions::cloud_simulated(path, "pants-storage-golden", "database/")
            .with_memtable_size_limit(1024 * 1024)
            .with_memtable_flush_threshold(1024 * 1024)
            .background_compaction(false)
            .build()?,
    )
}

#[cfg(feature = "failpoints")]
fn default_column_family(engine: &Engine) -> Result<ColumnFamilyHandle, MidgeError> {
    engine
        .get_column_family("default")
        .ok_or_else(|| MidgeError::Internal("the default column family is missing".to_string()))
}

#[cfg(feature = "failpoints")]
fn commit_put(
    engine: &Engine,
    column_family: &ColumnFamilyHandle,
    key: &[u8],
    value: &[u8],
    write_options: WriteOptions,
) -> Result<(), MidgeError> {
    let mut transaction = engine.begin_tx(column_family.id(), TransactionMode::ReadWrite)?;
    transaction.put(key.to_vec(), value.to_vec(), None)?;
    transaction.commit(write_options)
}

#[cfg(feature = "failpoints")]
fn seed_numbered_entries(
    engine: &Engine,
    column_family: &ColumnFamilyHandle,
    prefix: &str,
    count: usize,
) -> Result<(), MidgeError> {
    let mut transaction = engine.begin_tx(column_family.id(), TransactionMode::ReadWrite)?;
    for index in 0..count {
        transaction.put(
            format!("{prefix}/{index:06}").into_bytes(),
            format!("value-{index:06}").into_bytes(),
            None,
        )?;
    }
    transaction.commit(WriteOptions::sync())
}

#[cfg(feature = "failpoints")]
fn seed_structured_entries(
    engine: &Engine,
    column_family: &ColumnFamilyHandle,
) -> Result<(), MidgeError> {
    let mut transaction = engine.begin_tx(column_family.id(), TransactionMode::ReadWrite)?;
    for index in 0..384 {
        transaction.put(structured_key(index), vec![b'v'; 256], None)?;
    }
    transaction.delete_range(structured_key(40), structured_key(80))?;
    transaction.commit(WriteOptions::sync())
}

#[cfg(feature = "failpoints")]
fn structured_key(index: usize) -> Vec<u8> {
    format!("tenant/shared/static-segment/{index:04}").into_bytes()
}

#[cfg(feature = "failpoints")]
fn assert_structured_visibility(
    engine: &Engine,
    stale_column_family: &ColumnFamilyHandle,
) -> Result<(), Box<dyn std::error::Error>> {
    let column_family = engine
        .get_column_family(stale_column_family.name())
        .ok_or("the recovered structured column family is missing")?;
    let transaction = engine.begin_tx(column_family.id(), TransactionMode::ReadOnly)?;
    for (index, expected_present) in [(10, true), (50, false), (100, true)] {
        let value = transaction.get(&structured_key(index))?;
        if value.is_some() != expected_present {
            return Err(format!(
                "structured key {index} presence was {}, expected {expected_present}",
                value.is_some()
            )
            .into());
        }
        if let Some(value) = value {
            if value.as_ref() != [b'v'; 256] {
                return Err(format!("structured key {index} had an unexpected value").into());
            }
        }
    }
    Ok(())
}

#[cfg(feature = "failpoints")]
fn validate_structured_sst(path: &Path) -> Result<(), Box<dyn std::error::Error>> {
    use cntryl_midge::sst::traits::SstStateReader;

    let bytes = read_required_file(path)?;
    let footer_start = bytes
        .len()
        .checked_sub(cntryl_midge::sst::types::SST_FOOTER_SIZE)
        .ok_or("structured SST is shorter than its V4 footer")?;
    let footer = cntryl_midge::sst::types::Footer::decode(&bytes[footer_start..])?;
    if footer.meta_index_handle.size == 0 || footer.index_handle.size == 0 {
        return Err("structured SST has an empty metadata or index handle".into());
    }
    if footer.trie_handle.is_none() || footer.block_bloom_handle.is_none() {
        return Err("structured SST is missing its trie or block bloom handle".into());
    }

    let index_block = block_bytes(&bytes, footer.index_handle)?;
    let decoded_index = cntryl_midge::sst::compression::decompress_block_with_trailer(index_block)?;
    let index_entries = count_index_entries(&decoded_index)?;
    if index_entries < 2 {
        return Err(format!(
            "structured SST contains {index_entries} data block index entries; expected at least 2"
        )
        .into());
    }

    let mut reader = cntryl_midge::sst::fs::SstFileIo::open_with_real_fs(path)?;
    reader.load_block_bloom()?;
    let summary = reader.summary()?;
    if summary.size_bytes != u64::try_from(bytes.len())? {
        return Err("structured SST summary size did not match the file".into());
    }
    let tombstones = reader.range_tombstones();
    if tombstones.len() != 1
        || tombstones[0].start != structured_key(40)
        || tombstones[0].end != structured_key(80)
    {
        return Err("structured SST range tombstone did not round-trip".into());
    }

    Ok(())
}

#[cfg(feature = "failpoints")]
fn block_bytes(
    bytes: &[u8],
    handle: cntryl_midge::sst::types::BlockHandle,
) -> Result<&[u8], Box<dyn std::error::Error>> {
    let start = usize::try_from(handle.offset)?;
    let size = usize::try_from(handle.size)?;
    let end = start.checked_add(size).ok_or("SST block handle overflow")?;
    let encoded = bytes
        .get(start..end)
        .ok_or("SST block handle exceeds the file")?;
    let length_bytes = encoded
        .get(..4)
        .ok_or("SST block is shorter than its length prefix")?;
    let payload_length = usize::try_from(u32::from_le_bytes(length_bytes.try_into()?))?;
    let payload = encoded
        .get(4..)
        .ok_or("SST block is shorter than its length prefix")?;
    if payload.len() != payload_length {
        return Err("SST block length prefix does not match its handle".into());
    }
    Ok(payload)
}

#[cfg(feature = "failpoints")]
fn count_index_entries(bytes: &[u8]) -> Result<usize, Box<dyn std::error::Error>> {
    let mut offset = 0_usize;
    let mut count = 0_usize;
    while offset < bytes.len() {
        let length_end = offset.checked_add(4).ok_or("SST index offset overflow")?;
        let length_bytes = bytes
            .get(offset..length_end)
            .ok_or("SST index has a truncated key length")?;
        let key_length = u32::from_le_bytes(length_bytes.try_into()?) as usize;
        let entry_size = 4_usize
            .checked_add(key_length)
            .and_then(|size| size.checked_add(16))
            .ok_or("SST index entry size overflow")?;
        offset = offset
            .checked_add(entry_size)
            .ok_or("SST index offset overflow")?;
        if offset > bytes.len() {
            return Err("SST index has a truncated entry".into());
        }
        count = count.saturating_add(1);
    }
    Ok(count)
}

#[cfg(feature = "failpoints")]
fn validate_wal_frames(bytes: &[u8]) -> Result<(u64, u64, usize), Box<dyn std::error::Error>> {
    if bytes.is_empty() {
        return Err("WAL artifact is empty".into());
    }

    let mut offset = 0_usize;
    let mut max_sequence = 0_u64;
    let mut writer_epoch = None;
    let mut records = 0_usize;
    while offset < bytes.len() {
        let header_end = offset
            .checked_add(cntryl_midge::wal::frame::WAL_FRAME_HEADER_LEN)
            .ok_or("WAL frame offset overflow")?;
        let header = bytes
            .get(offset..header_end)
            .ok_or("WAL has a truncated frame header")?;
        let (payload_length, expected_crc) = cntryl_midge::wal::frame::decode_frame_header(header)?;
        let payload_end = header_end
            .checked_add(payload_length)
            .ok_or("WAL payload offset overflow")?;
        let payload = bytes
            .get(header_end..payload_end)
            .ok_or("WAL has a truncated frame payload")?;
        cntryl_midge::wal::frame::verify_frame_crc(payload, expected_crc)?;
        let record = cntryl_midge::wal::encoding::decode(Bytes::copy_from_slice(payload))?;
        if let Some(expected_epoch) = writer_epoch {
            if record.writer_epoch != expected_epoch {
                return Err("WAL artifact mixes writer epochs".into());
            }
        } else {
            writer_epoch = Some(record.writer_epoch);
        }
        if matches!(record.op, cntryl_midge::wal::WalOpKind::TxnBatch) {
            let batch_payload = record
                .value
                .as_ref()
                .ok_or("WAL transaction batch has no payload")?;
            let batch =
                cntryl_midge::wal::encoding::decode_txn_batch_payload(&record, batch_payload)?;
            if batch.records.is_empty() {
                return Err("WAL transaction batch has no records".into());
            }
        }
        max_sequence = max_sequence.max(record.seq);
        records = records.saturating_add(1);
        offset = payload_end;
    }

    Ok((max_sequence, writer_epoch.unwrap_or_default(), records))
}

#[cfg(feature = "failpoints")]
fn validate_publication_catalog(
    catalog_bytes: &[u8],
    segment_bytes: &[u8],
    max_sequence: u64,
    writer_epoch: u64,
) -> Result<(), Box<dyn std::error::Error>> {
    let catalog: serde_json::Value = serde_json::from_slice(catalog_bytes)?;
    if catalog["format_version"] != 1 || catalog["fencing_epoch"] != writer_epoch {
        return Err("cloud WAL publication catalog has unexpected version or epoch".into());
    }
    let segments = catalog["segments"]
        .as_object()
        .ok_or("cloud WAL publication catalog segments is not an object")?;
    if segments.len() != 1 {
        return Err(format!(
            "cloud WAL publication catalog contains {} segments; expected 1",
            segments.len()
        )
        .into());
    }
    let segment = segments
        .get("1")
        .ok_or("cloud WAL publication catalog is missing segment 1")?;
    let expected_key = cntryl_midge::wal::segment_object_key(1, writer_epoch);
    if segment["segment_id"] != 1
        || segment["writer_epoch"] != writer_epoch
        || segment["max_sequence"] != max_sequence
        || segment["size_bytes"] != u64::try_from(segment_bytes.len())?
        || segment["content_crc32c"] != u64::from(crc32c::crc32c(segment_bytes))
        || segment["object_key"] != expected_key
    {
        return Err("cloud WAL publication catalog does not describe its segment".into());
    }
    Ok(())
}

#[cfg(feature = "failpoints")]
fn validate_local_lease(bytes: &[u8]) -> Result<(), Box<dyn std::error::Error>> {
    let fields = parse_line_record(bytes, &["epoch", "holder_id", "acquired_at"])?;
    fields["epoch"].parse::<u64>()?;
    chrono::DateTime::parse_from_rfc3339(fields["acquired_at"])?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn validate_cloud_lease(bytes: &[u8]) -> Result<(), Box<dyn std::error::Error>> {
    let fields = parse_line_record(
        bytes,
        &[
            "epoch",
            "holder_id",
            "owner_token",
            "acquired_at",
            "expires_at",
        ],
    )?;
    fields["epoch"].parse::<u64>()?;
    uuid::Uuid::parse_str(fields["owner_token"])?;
    chrono::DateTime::parse_from_rfc3339(fields["acquired_at"])?;
    chrono::DateTime::parse_from_rfc3339(fields["expires_at"])?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn parse_line_record<'a>(
    bytes: &'a [u8],
    required_fields: &[&str],
) -> Result<std::collections::BTreeMap<&'a str, &'a str>, Box<dyn std::error::Error>> {
    let text = std::str::from_utf8(bytes)?;
    let fields = text
        .lines()
        .map(|line| {
            line.split_once(": ")
                .ok_or_else(|| format!("record line has no field separator: {line}"))
        })
        .collect::<Result<std::collections::BTreeMap<_, _>, _>>()?;
    for required in required_fields {
        if fields.get(required).is_none_or(|value| value.is_empty()) {
            return Err(format!("record is missing required field '{required}'").into());
        }
    }
    Ok(fields)
}

#[cfg(feature = "failpoints")]
fn validate_ddl_pair(
    registry_bytes: &[u8],
    prepare_bytes: &[u8],
) -> Result<(), Box<dyn std::error::Error>> {
    let registry: serde_json::Value = serde_json::from_slice(registry_bytes)?;
    let prepare: serde_json::Value = serde_json::from_slice(prepare_bytes)?;
    if registry["epoch"] != 1
        || registry["operations"].as_array().map(Vec::len) != Some(1)
        || registry["column_families"].as_array().map(Vec::len) != Some(1)
    {
        return Err("DDL registry does not contain the committed golden operation".into());
    }
    let op_id = prepare["op_id"]
        .as_str()
        .ok_or("DDL prepare is missing its operation id")?;
    uuid::Uuid::parse_str(op_id)?;
    let operation = &registry["operations"][0];
    let column_family = &registry["column_families"][0];
    if prepare["expected_remote_epoch"] != 0
        || operation["op_id"] != op_id
        || operation["edit"] != prepare["edit"]
        || prepare["edit"]["CreateColumnFamily"]["name"] != "fixture"
        || column_family["name"] != "fixture"
    {
        return Err("DDL registry and prepare do not describe the same golden operation".into());
    }
    Ok(())
}

#[cfg(feature = "failpoints")]
fn validate_json_object(bytes: &[u8], description: &str) -> Result<(), Box<dyn std::error::Error>> {
    if !serde_json::from_slice::<serde_json::Value>(bytes)?.is_object() {
        return Err(format!("{description} is not a JSON object").into());
    }
    Ok(())
}

#[cfg(feature = "failpoints")]
fn validate_nonempty_json_array(
    bytes: &[u8],
    description: &str,
) -> Result<(), Box<dyn std::error::Error>> {
    let value: serde_json::Value = serde_json::from_slice(bytes)?;
    if value.as_array().is_none_or(Vec::is_empty) {
        return Err(format!("{description} is not a nonempty JSON array").into());
    }
    Ok(())
}

#[cfg(feature = "failpoints")]
fn prepare_empty_directory(path: &Path) -> Result<(), Box<dyn std::error::Error>> {
    if path.exists() && fs::read_dir(path)?.next().is_some() {
        return Err(format!(
            "storage golden output directory '{}' is not empty",
            path.display()
        )
        .into());
    }
    fs::create_dir_all(path)?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn read_required_file(path: &Path) -> Result<Vec<u8>, Box<dyn std::error::Error>> {
    let bytes = fs::read(path)
        .map_err(|error| format!("failed to read required file '{}': {error}", path.display()))?;
    if bytes.is_empty() {
        return Err(format!("required file '{}' is empty", path.display()).into());
    }
    Ok(bytes)
}

#[cfg(feature = "failpoints")]
fn write_output_file(path: PathBuf, bytes: &[u8]) -> Result<(), Box<dyn std::error::Error>> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(&path, bytes)
        .map_err(|error| format!("failed to write output file '{}': {error}", path.display()))?;
    Ok(())
}

#[cfg(feature = "failpoints")]
fn single_file_with_extension(
    directory: &Path,
    extension: &str,
) -> Result<PathBuf, Box<dyn std::error::Error>> {
    let mut matches = fs::read_dir(directory)?
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| {
            path.is_file()
                && path
                    .extension()
                    .is_some_and(|candidate| candidate.eq_ignore_ascii_case(extension))
        })
        .collect::<Vec<_>>();
    matches.sort();
    if matches.len() != 1 {
        return Err(format!(
            "expected one .{extension} file in '{}', found {}",
            directory.display(),
            matches.len()
        )
        .into());
    }
    Ok(matches.remove(0))
}

#[cfg(feature = "failpoints")]
fn copy_directory(source: &Path, target: &Path) -> Result<(), Box<dyn std::error::Error>> {
    if target.exists() {
        return Err(format!("copy target '{}' already exists", target.display()).into());
    }
    fs::create_dir_all(target)?;
    let mut entries = fs::read_dir(source)?.collect::<Result<Vec<_>, _>>()?;
    entries.sort_by_key(std::fs::DirEntry::file_name);
    for entry in entries {
        let source_path = entry.path();
        let target_path = target.join(entry.file_name());
        if entry.file_type()?.is_dir() {
            copy_directory(&source_path, &target_path)?;
        } else if entry.file_type()?.is_file() {
            fs::copy(&source_path, &target_path)?;
        } else {
            return Err(format!(
                "database fixture contains unsupported entry '{}'",
                source_path.display()
            )
            .into());
        }
    }
    Ok(())
}
