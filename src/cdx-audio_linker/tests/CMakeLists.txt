# Unit tests for the pure, platform-independent pieces of the engine
# (containers and DSP) and the config (de)serialization. No audio API is
# touched here, so these run on any MSVC toolchain -- including the CI
# runner -- in a fraction of a second.

function(audio_linker_test name)
    add_executable(${name} ${name}.cpp ${ARGN})
    target_include_directories(${name} PRIVATE
        ${CMAKE_SOURCE_DIR}/include ${CMAKE_SOURCE_DIR}/third_party/json ${CMAKE_CURRENT_SOURCE_DIR})
    target_compile_definitions(${name} PRIVATE NOMINMAX UNICODE _UNICODE WIN32_LEAN_AND_MEAN)
    target_compile_options(${name} PRIVATE /utf-8)
    add_test(NAME ${name} COMMAND ${name})
endfunction()

audio_linker_test(test_ringbuffer)
audio_linker_test(test_resampler)
audio_linker_test(test_biquad)
audio_linker_test(test_render_dsp)
audio_linker_test(test_config ${CMAKE_SOURCE_DIR}/src/Config.cpp ${CMAKE_SOURCE_DIR}/src/Log.cpp)
audio_linker_test(test_migration ${CMAKE_SOURCE_DIR}/src/LegacyMigration.cpp)
audio_linker_test(test_bus_vectors)
target_include_directories(test_bus_vectors PRIVATE ${CMAKE_SOURCE_DIR})
target_compile_definitions(test_bus_vectors PRIVATE CDX_BUS_VECTORS="${CMAKE_SOURCE_DIR}/cdx/bus-vectors.json")
target_link_libraries(test_bus_vectors PRIVATE shell32 ole32)
